﻿// Copyright 2014 Serilog Contributors
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Amazon.Kinesis.Model;
using Serilog.Debugging;

namespace Serilog.Sinks.AmazonKinesis
{
    class HttpLogShipper : IDisposable
    {
        private readonly KinesisSinkState _state;

        readonly int _batchPostingLimit;
        readonly Timer _timer;
        readonly TimeSpan _period;
        readonly object _stateLock = new object();
        volatile bool _unloading;
        readonly string _bookmarkFilename;
        readonly string _logFolder;
        readonly string _candidateSearchPath;
        public event EventHandler<LogSendErrorEventArgs> LogSendError;

        public HttpLogShipper(KinesisSinkState state)
        {
            _state = state;
            _period = _state.Options.BufferLogShippingInterval ?? TimeSpan.FromSeconds(5);
            _batchPostingLimit = _state.Options.BatchPostingLimit;
            _bookmarkFilename = Path.GetFullPath(_state.Options.BufferBaseFilename + ".bookmark");
            _logFolder = Path.GetDirectoryName(_bookmarkFilename);
            _candidateSearchPath = Path.GetFileName(_state.Options.BufferBaseFilename) + "*.json";

            _timer = new Timer(s => OnTick());

            AppDomain.CurrentDomain.DomainUnload += OnAppDomainUnloading;
            AppDomain.CurrentDomain.ProcessExit += OnAppDomainUnloading;

            SetTimer();
        }

        void OnAppDomainUnloading(object sender, EventArgs e)
        {
            CloseAndFlush();
        }

        void OnLogSendError(LogSendErrorEventArgs e)
        {
            var handler = LogSendError;
            if (handler != null)
            {
                handler(this, e);
            }
        }

        void CloseAndFlush()
        {
            lock (_stateLock)
            {
                if (_unloading)
                    return;

                _unloading = true;
            }

            AppDomain.CurrentDomain.DomainUnload -= OnAppDomainUnloading;
            AppDomain.CurrentDomain.ProcessExit -= OnAppDomainUnloading;

            var wh = new ManualResetEvent(false);
            if (_timer.Dispose(wh))
                wh.WaitOne();

            OnTick();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Free resources held by the sink.
        /// </summary>
        /// <param name="disposing">If true, called because the object is being disposed; if false,
        /// the object is being disposed from the finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            CloseAndFlush();
        }

        void SetTimer()
        {
            // Note, called under _stateLock

            _timer.Change(_period, TimeSpan.FromDays(30));
        }

        void OnTick()
        {
            try
            {
                var count = 0;

                do
                {
                    // Locking the bookmark ensures that though there may be multiple instances of this
                    // class running, only one will ship logs at a time.

                    using (var bookmark = File.Open(_bookmarkFilename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        long startingOffset;
                        long nextLineBeginsAtOffset;
                        string currentFilePath;

                        TryReadBookmark(bookmark, out nextLineBeginsAtOffset, out currentFilePath);
                        SelfLog.WriteLine("Bookmark is currently at offset {0} in '{1}'", nextLineBeginsAtOffset, currentFilePath);

                        var fileSet = GetFileSet();

                        if (currentFilePath == null || !File.Exists(currentFilePath))
                        {
                            nextLineBeginsAtOffset = 0;
                            currentFilePath = fileSet.FirstOrDefault();
                        }

                        if (currentFilePath != null)
                        {
                            count = 0;

                            var records = new List<PutRecordsRequestEntry>();
                            using (var current = File.Open(currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                startingOffset = current.Position = nextLineBeginsAtOffset;

                                string nextLine;
                                while (count < _batchPostingLimit && TryReadLine(current, ref nextLineBeginsAtOffset, out nextLine))
                                {
                                    ++count;
                                    var bytes = Encoding.UTF8.GetBytes(nextLine);
                                    var record = new PutRecordsRequestEntry
                                    {
                                        PartitionKey = Guid.NewGuid().ToString(),
                                        Data = new MemoryStream(bytes)
                                    };
                                    records.Add(record);
                                }
                            }

                            if (count > 0)
                            {
                                var request = new PutRecordsRequest
                                {
                                    StreamName = _state.Options.StreamName,
                                    Records = records
                                };

                                SelfLog.WriteLine("Writing {0} records to kinesis", count);
                                PutRecordsResponse response = _state.KinesisClient.PutRecords(request);

                                SelfLog.WriteLine("Advancing bookmark from '{0}' to '{1}'", startingOffset, nextLineBeginsAtOffset);
                                WriteBookmark(bookmark, nextLineBeginsAtOffset, currentFilePath);

                                if (response.FailedRecordCount > 0)
                                {
                                    foreach (var record in response.Records)
                                    {
                                        SelfLog.WriteLine("Kinesis failed to index record in stream '{0}'. {1} {2} ", _state.Options.StreamName, record.ErrorCode, record.ErrorMessage);
                                    }
                                    // fire event
                                    OnLogSendError(new LogSendErrorEventArgs(string.Format("Error writing records to {0} ({1} of {2} records failed)", _state.Options.StreamName, response.FailedRecordCount, count), null));
                                }
                            }
                            else
                            {
                                SelfLog.WriteLine("Found no records to process");

                                // Only advance the bookmark if no other process has the
                                // current file locked, and its length is as we found it.

                                var bufferedFilesCount = fileSet.Length;
                                var isProcessingFirstFile = fileSet.First().Equals(currentFilePath, StringComparison.InvariantCultureIgnoreCase);

                                //SelfLog.WriteLine("BufferedFilesCount: {0}; IsProcessingFirstFile: {1}; IsFirstFileUnlocked: {2}", bufferedFilesCount, isProcessingFirstFile, isFirstFileUnlocked);

                                if (bufferedFilesCount == 2 && isProcessingFirstFile)
                                {
                                    TryWriteBookmark(currentFilePath, nextLineBeginsAtOffset, fileSet[1], bookmark);
                                }

                                if (bufferedFilesCount > 2)
                                {
                                    // Once there's a third file waiting to ship, we do our
                                    // best to move on, though a lock on the current file
                                    // will delay this.
                                    SelfLog.WriteLine("Deleting '{0}'", fileSet[0]);

                                    File.Delete(fileSet[0]);
                                }
                            }
                        }
                    }
                }

                while (count == _batchPostingLimit);
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine("Exception while emitting periodic batch from {0}: {1}", this, ex);
                OnLogSendError(new LogSendErrorEventArgs(string.Format("Error in shipping logs to '{0}' stream)", _state.Options.StreamName), ex));
            }
            finally
            {
                lock (_stateLock)
                {
                    if (!_unloading)
                        SetTimer();
                }
            }
        }

        static void TryWriteBookmark(string currentFilePath, long nextLineBeginsAtOffset, string bufferedFileName, FileStream bookmark)
        {
            try
            {
                using (var fileStream = File.Open(currentFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    if (fileStream.Length <= nextLineBeginsAtOffset)
                    {
                        SelfLog.WriteLine("Advancing bookmark from '{0}' to '{1}'", currentFilePath, bufferedFileName);
                        WriteBookmark(bookmark, 0, bufferedFileName);
                    }
                }
            }

            catch (IOException ex)
            {
                var errorCode = Marshal.GetHRForException(ex) & ((1 << 16) - 1);
                if (errorCode == 32)
                {
                    SelfLog.WriteLine("Log file {0} is locked by another process, bookmark is not advanced: {1}", currentFilePath, ex);
                }
                else if (errorCode == 33)
                {
                    SelfLog.WriteLine("Unexpected I/O exception while testing locked status of {0}: {1}", currentFilePath, ex);
                }
                else
                {
                    throw;
                }
            }

            catch (Exception ex)
            {
                SelfLog.WriteLine("Unexpected exception while testing locked status of {0}: {1}", currentFilePath, ex);
            }
        }

        static void WriteBookmark(FileStream bookmark, long nextLineBeginsAtOffset, string currentFile)
        {
            // Important not to dispose this StreamReader as the stream must remain open.
            var writer = new StreamWriter(bookmark);
            writer.WriteLine("{0}:::{1}", nextLineBeginsAtOffset, currentFile);
            writer.Flush();
        }

        // It would be ideal to chomp whitespace here, but not required.
        static bool TryReadLine(Stream current, ref long nextStart, out string nextLine)
        {
            var includesBom = nextStart == 0;

            if (current.Length <= nextStart)
            {
                nextLine = null;
                return false;
            }

            current.Position = nextStart;

            // Important not to dispose this StreamReader as the stream must remain open.
            var reader = new StreamReader(current, Encoding.UTF8, false, 128);
            nextLine = reader.ReadLine();

            if (nextLine == null)
                return false;

            nextStart += Encoding.UTF8.GetByteCount(nextLine) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (includesBom)
                nextStart += 3;

            return true;
        }

        static void TryReadBookmark(Stream bookmark, out long nextLineBeginsAtOffset, out string currentFile)
        {
            nextLineBeginsAtOffset = 0;
            currentFile = null;

            if (bookmark.Length != 0)
            {
                string current;
                // Important not to dispose this StreamReader as the stream must remain open.
                var reader = new StreamReader(bookmark, Encoding.UTF8, false, 128);
                current = reader.ReadLine();

                if (current != null)
                {
                    bookmark.Position = 0;
                    var parts = current.Split(new[] { ":::" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        nextLineBeginsAtOffset = long.Parse(parts[0]);
                        currentFile = parts[1];
                    }
                }

            }
        }

        string[] GetFileSet()
        {
            return Directory.GetFiles(_logFolder, _candidateSearchPath)
                .OrderBy(n => n)
                .ToArray();
        }
    }
}