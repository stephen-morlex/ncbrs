using System.Text.Json;
using System.Text.Json.Serialization;

namespace NCBRS.Client.Network;

/// <summary>
/// How the device writes and reads the centre's JSON: camelCase, enums as
/// their names — the same conventions the API serialises with, so what the
/// device sends reads the way the API's own documentation describes it, and
/// what comes back parses without a second set of rules.
/// </summary>
public static class ClientJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
