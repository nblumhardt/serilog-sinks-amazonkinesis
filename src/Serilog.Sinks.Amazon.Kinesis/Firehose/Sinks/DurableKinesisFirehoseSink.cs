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
using Amazon.KinesisFirehose;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.Amazon.Kinesis.Common;
using Serilog.Sinks.RollingFile;

namespace Serilog.Sinks.Amazon.Kinesis.Firehose.Sinks
{
    sealed class DurableKinesisFirehoseSink : ILogEventSink, IDisposable
    {
        readonly HttpLogShipper _shipper;
        readonly RollingFileSink _sink;
        EventHandler<LogSendErrorEventArgs> _logSendErrorHandler;

        public DurableKinesisFirehoseSink(KinesisFirehoseSinkOptions options, IAmazonKinesisFirehose kinesisFirehoseClient)
        {
            var state = new KinesisSinkState(options, kinesisFirehoseClient);

            if (string.IsNullOrWhiteSpace(options.BufferBaseFilename))
            {
                throw new ArgumentException("Cannot create the durable Amazon Kinesis Firehose sink without a buffer base file name.");
            }

            _sink = new RollingFileSink(
               options.BufferBaseFilename + "-{Date}.json",
               state.DurableFormatter,
               options.BufferFileSizeLimitBytes,
               null,
               shared: options.Shared);

            _shipper = new HttpLogShipper(state);

            _logSendErrorHandler = options.OnLogSendError;
            if (_logSendErrorHandler != null)
            {
                _shipper.LogSendError += _logSendErrorHandler;
            }
        }

        public void Emit(LogEvent logEvent)
        {
            _sink.Emit(logEvent);
            _shipper.Emit();
        }

        public void Dispose()
        {
            _sink.Dispose();
            _shipper.Dispose();

            if (_logSendErrorHandler != null)
            {
                _shipper.LogSendError -= _logSendErrorHandler;
                _logSendErrorHandler = null;
            }
        }
    }
}