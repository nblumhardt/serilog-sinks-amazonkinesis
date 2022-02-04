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
using Amazon.Kinesis;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.Amazon.Kinesis.Common;
using Serilog.Sinks.Amazon.Kinesis.Stream.Sinks;

namespace Serilog.Sinks.Amazon.Kinesis.Stream
{
    /// <summary>
    /// Adds the WriteTo.AmazonKinesis() extension method to <see cref="LoggerConfiguration"/>.
    /// </summary>
    public static class KinesisLoggerConfigurationExtensions
    {
        /// <summary>
        /// Adds a sink that writes log events as documents to Amazon Kinesis.
        /// </summary>
        /// <param name="loggerConfiguration">The logger configuration.</param>
        /// <param name="options"></param>
        /// <param name="kinesisClient"></param>
        /// <returns>Logger configuration, allowing configuration to continue.</returns>
        /// <exception cref="ArgumentNullException">A required parameter is null.</exception>
        public static LoggerConfiguration AmazonKinesis(
            this LoggerSinkConfiguration loggerConfiguration,
            KinesisStreamSinkOptions options,IAmazonKinesis kinesisClient)
        {
            if (loggerConfiguration == null) throw new ArgumentNullException("loggerConfiguration");
            if (options == null) throw new ArgumentNullException("options");
            if (kinesisClient == null) throw new ArgumentNullException("kinesisClient");

            ILogEventSink sink;
            if (options.BufferBaseFilename == null)
            {
                sink = new KinesisSink(options, kinesisClient);
            }
            else
            {
                sink = new DurableKinesisSink(options, kinesisClient);
            }

            return loggerConfiguration.Sink(sink, options.MinimumLogEventLevel ?? LevelAlias.Minimum);
        }

        
        /// <summary>
        /// Adds a sink that writes log events as documents to Amazon Kinesis.
        /// </summary>
        /// <param name="loggerConfiguration">The logger configuration.</param>
        /// <param name="kinesisClient"></param>
        /// <param name="streamName"></param>
        /// <param name="bufferBaseFilename"></param>
        /// <param name="bufferFileSizeLimitBytes"></param>
        /// <param name="batchPostingLimit"></param>
        /// <param name="period"></param>
        /// <param name="minimumLogEventLevel"></param>
        /// <param name="onLogSendError"></param>
        /// <param name="shared"></param>
        /// <returns>Logger configuration, allowing configuration to continue.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static LoggerConfiguration AmazonKinesis(
            this LoggerSinkConfiguration loggerConfiguration,
            IAmazonKinesis kinesisClient,
            string streamName,
            string bufferBaseFilename = null,
            int? bufferFileSizeLimitBytes = null,
            int? batchPostingLimit = null,
            TimeSpan? period = null,
            LogEventLevel? minimumLogEventLevel = null,
            ITextFormatter customFormatter = null,
            EventHandler<LogSendErrorEventArgs> onLogSendError = null,
            bool shared = false)
        {
            if (streamName == null) throw new ArgumentNullException("streamName");

            var options = new KinesisStreamSinkOptions(streamName: streamName)
            {
                BufferFileSizeLimitBytes = bufferFileSizeLimitBytes,
                BufferBaseFilename = bufferBaseFilename == null ? null : bufferBaseFilename + ".stream",
                Period = period ?? KinesisSinkOptionsBase.DefaultPeriod,
                BatchPostingLimit = batchPostingLimit ?? KinesisSinkOptionsBase.DefaultBatchPostingLimit,
                MinimumLogEventLevel = minimumLogEventLevel ?? LevelAlias.Minimum,
                OnLogSendError = onLogSendError,
                CustomDurableFormatter = customFormatter,
                Shared = shared
            };

            return AmazonKinesis(loggerConfiguration, options, kinesisClient);
        }
    }
}
