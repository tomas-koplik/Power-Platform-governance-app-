using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ppgsm.Data;

internal sealed class JsonDocumentValueConverter : ValueConverter<JsonDocument, string>
{
    public static JsonDocumentValueConverter Instance { get; } = new();
    public static ValueConverter<JsonDocument?, string?> NullableInstance { get; } = new(
        value => value == null ? null : value.RootElement.GetRawText(),
        value => value == null ? null : JsonDocument.Parse(value, default));

    private JsonDocumentValueConverter()
        : base(value => value.RootElement.GetRawText(), value => JsonDocument.Parse(value, default))
    {
    }
}