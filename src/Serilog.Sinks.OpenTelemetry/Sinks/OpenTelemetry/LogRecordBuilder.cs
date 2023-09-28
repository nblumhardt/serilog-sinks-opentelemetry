// Copyright 2022 Serilog Contributors
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

// ReSharper disable PossibleMultipleEnumeration

using System.Globalization;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.OpenTelemetry.Formatting;
using Serilog.Sinks.OpenTelemetry.ProtocolHelpers;

namespace Serilog.Sinks.OpenTelemetry;

static class LogRecordBuilder
{
    public static LogRecord ToLogRecord(LogEvent logEvent, IFormatProvider? formatProvider, IncludedData includedFields, ActivityContextCollector activityContextCollector)
    {
        var logRecord = new LogRecord();

        ProcessProperties(logRecord, logEvent);
        ProcessTimestamp(logRecord, logEvent);
        ProcessMessage(logRecord, logEvent, includedFields, formatProvider);
        ProcessLevel(logRecord, logEvent);
        ProcessException(logRecord, logEvent);
        ProcessIncludedFields(logRecord, logEvent, includedFields, activityContextCollector);

        return logRecord;
    }

    public static void ProcessMessage(LogRecord logRecord, LogEvent logEvent, IncludedData includedFields, IFormatProvider? formatProvider)
    {
        if (!includedFields.HasFlag(IncludedData.TemplateBody))
        {
            var renderedMessage = CleanMessageTemplateFormatter.Format(logEvent.MessageTemplate, logEvent.Properties, formatProvider);

            if (renderedMessage.Trim() != "")
            {
                logRecord.Body = new AnyValue
                {
                    StringValue = renderedMessage
                };
            }
        }
        else if (includedFields.HasFlag(IncludedData.TemplateBody) && logEvent.MessageTemplate.Text.Trim() != "")
        {
            logRecord.Body = new AnyValue
            {
                StringValue = logEvent.MessageTemplate.Text
            };
        }
    }

    public static void ProcessLevel(LogRecord logRecord, LogEvent logEvent)
    {
        var level = logEvent.Level;
        logRecord.SeverityText = level.ToString();
        logRecord.SeverityNumber = PrimitiveConversions.ToSeverityNumber(level);
    }

    public static void ProcessProperties(LogRecord logRecord, LogEvent logEvent)
    {
        foreach (var property in logEvent.Properties)
        {
            var v = PrimitiveConversions.ToOpenTelemetryAnyValue(property.Value);
            logRecord.Attributes.Add(PrimitiveConversions.NewAttribute(property.Key, v));
        }
    }

    public static void ProcessTimestamp(LogRecord logRecord, LogEvent logEvent)
    {
        logRecord.TimeUnixNano = PrimitiveConversions.ToUnixNano(logEvent.Timestamp);
        logRecord.ObservedTimeUnixNano = logRecord.TimeUnixNano;
    }

    public static void ProcessException(LogRecord logRecord, LogEvent logEvent)
    {
        var ex = logEvent.Exception;
        if (ex != null)
        {
            var attrs = logRecord.Attributes;

            attrs.Add(PrimitiveConversions.NewStringAttribute(SemanticConventions.AttributeExceptionType, ex.GetType().ToString()));

            if (ex.Message != "")
            {
                attrs.Add(PrimitiveConversions.NewStringAttribute(SemanticConventions.AttributeExceptionMessage, ex.Message));
            }

            if (ex.ToString() != "")
            {
                attrs.Add(PrimitiveConversions.NewStringAttribute(SemanticConventions.AttributeExceptionStacktrace, ex.ToString()));
            }
        }
    }

    static void ProcessIncludedFields(LogRecord logRecord, LogEvent logEvent, IncludedData includedFields, ActivityContextCollector activityContextCollector)
    {
        if ((includedFields & (IncludedData.TraceIdField | IncludedData.SpanIdField)) != IncludedData.None)
        {
            var activityContext = activityContextCollector.GetFor(logEvent);
            
            if (activityContext is var (activityTraceId, activitySpanId))
            {
                if ((includedFields & IncludedData.TraceIdField) != IncludedData.None)
                {
                    logRecord.TraceId = PrimitiveConversions.ToOpenTelemetryTraceId(activityTraceId.ToHexString());
                }

                if ((includedFields & IncludedData.SpanIdField) != IncludedData.None)
                {
                    logRecord.SpanId = PrimitiveConversions.ToOpenTelemetrySpanId(activitySpanId.ToHexString());
                }
            }
        }

        if ((includedFields & IncludedData.MessageTemplateTextAttribute) != IncludedData.None)
        {
            logRecord.Attributes.Add(PrimitiveConversions.NewAttribute(SemanticConventions.AttributeMessageTemplateText, new()
            {
                StringValue = logEvent.MessageTemplate.Text
            }));
        }

        if ((includedFields & IncludedData.MessageTemplateMD5HashAttribute) != IncludedData.None)
        {
            logRecord.Attributes.Add(PrimitiveConversions.NewAttribute(SemanticConventions.AttributeMessageTemplateMD5Hash, new()
            {
                StringValue = PrimitiveConversions.Md5Hash(logEvent.MessageTemplate.Text)
            }));
        }

        if ((includedFields & IncludedData.MessageTemplateRenderingsAttribute) != IncludedData.None)
        {
            var tokensWithFormat = logEvent.MessageTemplate.Tokens
                .OfType<PropertyToken>()
                .Where(pt => pt.Format != null);

            // Better not to allocate an array in the 99.9% of cases where this is false
            if (tokensWithFormat.Any())
            {
                var renderings = new ArrayValue();

                foreach (var propertyToken in tokensWithFormat)
                {
                    var space = new StringWriter();
                    propertyToken.Render(logEvent.Properties, space, CultureInfo.InvariantCulture);
                    renderings.Values.Add(new AnyValue { StringValue = space.ToString() });
                }
                
                logRecord.Attributes.Add(PrimitiveConversions.NewAttribute(
                    SemanticConventions.AttributeMessageTemplateRenderings,
                    new AnyValue { ArrayValue = renderings }));
            }
        }
    }
}
