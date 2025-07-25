// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Shared;
using Type = System.Type;

namespace Microsoft.AspNetCore.Grpc.JsonTranscoding.Internal.Json;

internal sealed class MessageTypeInfoResolver : IJsonTypeInfoResolver
{
    private readonly JsonContext _context;

    public MessageTypeInfoResolver(JsonContext context)
    {
        _context = context;
    }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        if (!IsStandardMessage(type, out var messageDescriptor))
        {
            return null;
        }

        var typeInfo = JsonTypeInfo.CreateJsonTypeInfo(type, options);
        typeInfo.CreateObject = () => Activator.CreateInstance(type)!;

        var fields = messageDescriptor.Fields.InFieldNumberOrder();

        // The field map can have multiple entries for a property:
        // 1. The JSON field name, e.g. firstName. This is used to serialize and deserialize JSON.
        // 2. The original field name, e.g. first_name. This might be different. It is only used to deserialize JSON.
        var mappings = CreateJsonFieldMap(fields);

        foreach (var field in fields)
        {
            var propertyInfo = CreatePropertyInfo(typeInfo, field.JsonName, field, isSerializable: true);
            typeInfo.Properties.Add(propertyInfo);

            // We have a property for reading and writing the JSON name so remove from mappings.
            mappings.Remove(field.JsonName);
        }

        // Fields have two mappings: the original field name and the camelcased JSON name.
        // The JSON name can also be customized in proto with json_name option.
        // Remaining mappings are for extra setter only properties.
        foreach (var mapping in mappings)
        {
            var propertyInfo = CreatePropertyInfo(typeInfo, mapping.Key, mapping.Value, isSerializable: false);
            typeInfo.Properties.Add(propertyInfo);
        }

        return typeInfo;
    }

    private bool IsStandardMessage(Type type, [NotNullWhen(true)] out MessageDescriptor? messageDescriptor)
    {
        if (type.IsInterface || !typeof(IMessage).IsAssignableFrom(type))
        {
            messageDescriptor = null;
            return false;
        }

        messageDescriptor = (MessageDescriptor?) _context.DescriptorRegistry.FindDescriptorByType(type);
        if (messageDescriptor == null)
        {
            throw new InvalidOperationException("Couldn't resolve descriptor for message type: " + type);
        }

        // Wrappers and well known types are handled by converters.
        if (ServiceDescriptorHelpers.IsWrapperType(messageDescriptor))
        {
            return false;
        }
        if (JsonConverterHelper.WellKnownTypeNames.ContainsKey(messageDescriptor.FullName))
        {
            return false;
        }

        return true;
    }

    private JsonPropertyInfo CreatePropertyInfo(JsonTypeInfo typeInfo, string name, FieldDescriptor field, bool isSerializable)
    {
        var propertyInfo = typeInfo.CreateJsonPropertyInfo(
            JsonConverterHelper.GetFieldType(field),
            name);

        // A property with a wrapper type is usually the underlying type on the DTO.
        // For example, a field of type google.protobuf.StringValue will have a property of type string.
        // However, the wrapper type is exposed if someone manually creates a DTO with it, or there is a problem
        // detecting wrapper type in code generation. For example, https://github.com/protocolbuffers/protobuf/issues/22744
        FieldDescriptor? wrapperTypeValueField = null;
        if (field.FieldType == FieldType.Message && ServiceDescriptorHelpers.IsWrapperType(field.MessageType))
        {
            var property = field.ContainingType.ClrType.GetProperty(field.PropertyName);

            // Check if the property type is the same as the field type. This means the property is StringValue, et al,
            // and additional conversion is required.
            if (property != null && property.PropertyType == field.MessageType.ClrType)
            {
                wrapperTypeValueField = field.MessageType.FindFieldByName("value");
            }
        }

        propertyInfo.ShouldSerialize = (o, v) =>
        {
            // Properties that don't have this flag set are only used to deserialize incoming JSON.
            if (!isSerializable)
            {
                return false;
            }
            return JsonConverterHelper.ShouldFormatFieldValue((IMessage)o, field, v, !_context.Settings.IgnoreDefaultValues);
        };
        propertyInfo.Get = (o) =>
        {
            var value = field.Accessor.GetValue((IMessage)o);
            if (wrapperTypeValueField != null && value is IMessage wrapperMessage)
            {
                return wrapperTypeValueField.Accessor.GetValue(wrapperMessage);
            }

            return value;
        };

        if (field.IsMap || field.IsRepeated)
        {
            // Collection properties are read-only. Populate values into existing collection.
            propertyInfo.ObjectCreationHandling = JsonObjectCreationHandling.Populate;
        }
        else
        {
            propertyInfo.Set = GetSetMethod(field, wrapperTypeValueField);
        }

        return propertyInfo;
    }

    private static Action<object, object?> GetSetMethod(FieldDescriptor field, FieldDescriptor? wrapperTypeValueField)
    {
        Debug.Assert(!field.IsRepeated && !field.IsMap, "Collections shouldn't have a setter.");

        if (field.RealContainingOneof != null)
        {
            return (o, v) =>
            {
                var caseField = field.RealContainingOneof.Accessor.GetCaseFieldDescriptor((IMessage)o);
                if (caseField != null)
                {
                    throw new InvalidOperationException($"Multiple values specified for oneof {field.RealContainingOneof.Name}.");
                }

                SetFieldValue(field, wrapperTypeValueField, (IMessage)o, v);
            };
        }

        return (o, v) =>
        {
            SetFieldValue(field, wrapperTypeValueField, (IMessage)o, v);
        };

        static void SetFieldValue(FieldDescriptor field, FieldDescriptor? wrapperTypeValueField, IMessage m, object? v)
        {
            if (v != null)
            {
                // This field exposes a wrapper type. Need to create a wrapper instance and set the value on it.
                if (wrapperTypeValueField != null && v is not IMessage)
                {
                    var wrapper = (IMessage)Activator.CreateInstance(field.MessageType.ClrType)!;
                    wrapperTypeValueField.Accessor.SetValue(wrapper, v);
                    v = wrapper;
                }

                field.Accessor.SetValue(m, v);
            }
            else
            {
                field.Accessor.Clear(m);
            }
        }
    }

    private static Dictionary<string, FieldDescriptor> CreateJsonFieldMap(IList<FieldDescriptor> fields)
    {
        var map = new Dictionary<string, FieldDescriptor>();
        // The ordering is important here: JsonName takes priority over Name,
        // which means we need to put JsonName values in the map after *all*
        // Name keys have been added. See https://github.com/protocolbuffers/protobuf/issues/11987
        foreach (var field in fields)
        {
            map[field.Name] = field;
        }
        foreach (var field in fields)
        {
            map[field.JsonName] = field;
        }
        return map;
    }
}
