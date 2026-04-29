namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// Determines how a variable's <c>Value</c> is stored and exposed to scripts.
/// </summary>
public enum VariableType
{
    /// <summary>Plain text string — passed as-is to scripts and the Octostache engine.</summary>
    Text = 0,

    /// <summary>
    /// Encrypted at rest using AES-256-GCM.
    /// The value is decrypted server-side during deployment planning and sent over
    /// the TLS-protected SignalR connection. It is redacted in deployment logs.
    /// </summary>
    Sensitive = 1,

    /// <summary>
    /// A list of strings stored as a JSON array (<c>["a","b","c"]</c>).
    /// <para>
    /// On the agent: exposed as <c>$OctopusArrays["VarName"]</c> (PowerShell array)
    /// and as <c>$OctopusParameters["VarName"]</c> (comma-joined, for back-compat).
    /// </para>
    /// <para>
    /// In Octostache: supports <c>#{each x in VarName}...#{/each}</c>,
    /// <c>#{VarName[i]}</c>, and <c>#{VarName | join "; "}</c>.
    /// </para>
    /// </summary>
    StringArray = 2,
}
