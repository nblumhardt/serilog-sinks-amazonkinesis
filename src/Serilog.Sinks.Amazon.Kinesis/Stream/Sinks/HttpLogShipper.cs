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
using System.Threading.Tasks;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Serilog.Debugging;
using Serilog.Sinks.Amazon.Kinesis.Common;

namespace Serilog.Sinks.Amazon.Kinesis.Stream.Sinks
{
    sealed class HttpLogShipper : HttpLogShipperBase<PutRecordsRequestEntry, PutRecordsResponse>, IDisposable
    {
        readonly IAmazonKinesis _kinesisClient;
        readonly Throttle _throttle;

        public HttpLogShipper(KinesisSinkState state) : base(state.Options,
            new LogReaderFactory(),
            new PersistedBookmarkFactory(),
            new LogShipperFileManager()
            )
        {
            _throttle = new Throttle(ShipLogs, state.Options.Period);
            _kinesisClient = state.KinesisClient;
        }

        public void Emit()
        {
            _throttle.ThrottleAction();
        }

        public void Dispose()
        {
            _throttle.Flush();
            _throttle.Stop();
            _throttle.Dispose();
        }

        protected override PutRecordsRequestEntry PrepareRecord(MemoryStream stream)
        {
            return new PutRecordsRequestEntry
            {
                PartitionKey = Guid.NewGuid().ToString(),
                Data = stream
            };
        }

        protected override PutRecordsResponse SendRecords(List<PutRecordsRequestEntry> records, out bool successful)
        {
            var request = new PutRecordsRequest
            {
                StreamName = _streamName,
                Records = records
            };

            SelfLog.WriteLine("Writing {0} records to kinesis", records.Count);
            var putRecordBatchTask = _kinesisClient.PutRecordsAsync(request);

            successful = putRecordBatchTask.GetAwaiter().GetResult().FailedRecordCount == 0;
            return putRecordBatchTask.Result;
        }

        protected override void HandleError(PutRecordsResponse response, int originalRecordCount)
        {
            foreach (var record in response.Records)
            {
                SelfLog.WriteLine("Kinesis failed to index record in stream '{0}'. {1} {2} ", _streamName, record.ErrorCode, record.ErrorMessage);
            }
            // fire event
            OnLogSendError(new LogSendErrorEventArgs(string.Format("Error writing records to {0} ({1} of {2} records failed)", _streamName, response.FailedRecordCount, originalRecordCount), null));
        }
    }
}