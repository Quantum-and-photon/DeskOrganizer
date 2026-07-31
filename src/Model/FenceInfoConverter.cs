using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskOrganizer.Model;

/// <summary>
/// FenceInfo 的自定义 JSON 转换器，处理旧版 "files" 与新版 "filePaths" 属性名映射，
/// 同时兼容 "locked"/"isLocked" 双键名。
/// </summary>
public class FenceInfoConverter : JsonConverter<FenceInfo>
{
    private const string PropertyFiles = "files";
    private const string PropertyFilePaths = "filePaths";
    private const string PropertyLocked = "locked";
    private const string PropertyIsLocked = "isLocked";

    public override FenceInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected StartObject, got {reader.TokenType}.");

        var fence = new FenceInfo();
        var filePaths = new List<string>();
        bool? lockedValue = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException($"Expected PropertyName, got {reader.TokenType}.");

            var propertyName = reader.GetString()?.ToLowerInvariant();

            reader.Read(); // advance to value

            switch (propertyName)
            {
                case "id":
                    fence.Id = reader.GetString() ?? fence.Id;
                    break;

                case "name":
                    fence.Name = reader.GetString() ?? string.Empty;
                    break;

                case "x":
                    fence.X = reader.GetDouble();
                    break;

                case "y":
                    fence.Y = reader.GetDouble();
                    break;

                case "width":
                    fence.Width = reader.GetDouble();
                    break;

                case "height":
                    fence.Height = reader.GetDouble();
                    break;

                case "posx":
                    fence.PosX = reader.GetInt32();
                    break;

                case "posy":
                    fence.PosY = reader.GetInt32();
                    break;

                case PropertyLocked:
                case PropertyIsLocked:
                    lockedValue = reader.GetBoolean();
                    break;

                case "canminify":
                    fence.CanMinify = reader.GetBoolean();
                    break;

                case "titleheight":
                    fence.TitleHeight = reader.GetInt32();
                    break;

                case PropertyFiles:
                case PropertyFilePaths:
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType == JsonTokenType.String)
                                filePaths.Add(reader.GetString()!);
                        }
                    }
                    break;

                case "backgroundcolor":
                    fence.BackgroundColor = reader.GetString() ?? "#80FFFFFF";
                    break;

                case "opacity":
                    fence.Opacity = reader.GetDouble();
                    break;

                case "cornerradius":
                    fence.CornerRadius = reader.GetInt32();
                    break;

                case "iconsize":
                    fence.IconSize = reader.GetInt32();
                    break;

                case "createdat":
                    fence.CreatedAt = reader.GetDateTime();
                    break;

                case "modifiedat":
                    fence.ModifiedAt = reader.GetDateTime();
                    break;

                default:
                    // 跳过未知属性
                    reader.Skip();
                    break;
            }
        }

        // 合并文件路径
        if (filePaths.Count > 0)
            fence.FilePaths = filePaths;

        // 合并锁定状态
        if (lockedValue.HasValue)
            fence.Locked = lockedValue.Value;

        return fence;
    }

    public override void Write(Utf8JsonWriter writer, FenceInfo value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("width", value.Width);
        writer.WriteNumber("height", value.Height);
        writer.WriteNumber("posX", value.PosX);
        writer.WriteNumber("posY", value.PosY);
        writer.WriteBoolean("locked", value.Locked);
        writer.WriteBoolean("canMinify", value.CanMinify);
        writer.WriteNumber("titleHeight", value.TitleHeight);
        writer.WritePropertyName("filePaths");
        JsonSerializer.Serialize(writer, value.FilePaths, options);
        writer.WriteString("backgroundColor", value.BackgroundColor);
        writer.WriteNumber("opacity", value.Opacity);
        writer.WriteNumber("cornerRadius", value.CornerRadius);
        writer.WriteNumber("iconSize", value.IconSize);
        writer.WriteString("createdAt", value.CreatedAt);
        writer.WriteString("modifiedAt", value.ModifiedAt);

        writer.WriteEndObject();
    }
}
