using System.Dynamic;
using System.Text.Json;

namespace Agency.Harness.Tools;

internal sealed class JsonDynamicAccessor(JsonElement element) : DynamicObject
{
    public string? this[string propertyName]
    {
        get
        {
            if (element.TryGetProperty(propertyName, out JsonElement propertyValue) && propertyValue.ValueKind == JsonValueKind.String)
            {
                return propertyValue.GetString();
            }
            return null;
        }
    }

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        result = this[binder.Name];
        return true;
    }

    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        if (indexes.Length == 1 && indexes[0] is string propertyName)
        {
            result = this[propertyName];
            return true;
        }

        result = null;
        return false;
    }

    public override System.Collections.Generic.IEnumerable<string> GetDynamicMemberNames() =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Select(p => p.Name)
            : [];
}