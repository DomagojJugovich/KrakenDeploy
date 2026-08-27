using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Data.Encryption;

public static class PromptedVariableFormValuesCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(
        IReadOnlyDictionary<string, string> values,
        IReadOnlySet<string> sensitiveNames,
        IEncryptionService encryption)
    {
        var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            (sensitiveNames.Contains(name) ? sensitive : plain)[name] = value;
        }

        var payload = new PromptedVariableFormValues
        {
            Version = 1,
            Values = plain,
            SensitiveValuesEncrypted = sensitive.Count == 0
                ? null
                : encryption.Encrypt(JsonSerializer.Serialize(sensitive, JsonOptions)),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static Dictionary<string, string> Deserialize(string payloadJson, IEncryptionService encryption)
    {
        var payload = JsonSerializer.Deserialize<PromptedVariableFormValues>(payloadJson, JsonOptions)
            ?? throw new InvalidOperationException("The prompted-variable payload is invalid.");
        EnsureCurrent(payload);
        var result = new Dictionary<string, string>(payload.Values, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(payload.SensitiveValuesEncrypted))
        {
            var sensitive = JsonSerializer.Deserialize<Dictionary<string, string>>(
                encryption.Decrypt(payload.SensitiveValuesEncrypted), JsonOptions)
                ?? throw new InvalidOperationException("The sensitive prompted-variable payload is invalid.");
            foreach (var (name, value) in sensitive)
            {
                result[name] = value;
            }
        }
        return result;
    }

    public static string ReEncrypt(string payloadJson, Func<string, string> reEncrypt)
    {
        var payload = JsonSerializer.Deserialize<PromptedVariableFormValues>(payloadJson, JsonOptions)
            ?? throw new InvalidOperationException("The prompted-variable payload is invalid.");
        EnsureCurrent(payload);
        if (string.IsNullOrEmpty(payload.SensitiveValuesEncrypted))
        {
            return payloadJson;
        }
        payload.SensitiveValuesEncrypted = reEncrypt(payload.SensitiveValuesEncrypted);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static void EnsureCurrent(PromptedVariableFormValues payload)
    {
        if (payload.Version != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported prompted-variable payload version {payload.Version}.");
        }
    }
}
