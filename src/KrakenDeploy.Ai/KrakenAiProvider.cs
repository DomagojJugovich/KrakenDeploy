namespace KrakenDeploy.Ai;

/// <summary>
/// Pluggable LLM provider backing <see cref="IKrakenAi"/>. Each Space picks
/// one provider at runtime (Phase M11.A). Switching providers is a config
/// change, not a code change — the <see cref="KrakenAiClientFactory"/> hands
/// the right <c>Microsoft.Extensions.AI.IChatClient</c> adapter to the
/// wrapper, and the wrapper handles audit / sanitisation / budget uniformly.
/// </summary>
public enum KrakenAiProvider
{
    /// <summary>
    /// AI is off for this Space. <see cref="IKrakenAi"/> throws a clear
    /// <see cref="KrakenAiDisabledException"/> on any call. This is the
    /// default — admins must explicitly opt in.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Anthropic Claude via the official <c>Anthropic</c> NuGet
    /// (12.22+). Uses <c>AnthropicClient.AsIChatClient(model)</c> to plug
    /// into <c>Microsoft.Extensions.AI</c>. <strong>Default recommended
    /// provider</strong> — cleanest structured-output + tool-use story.
    /// </summary>
    Anthropic = 1,

    /// <summary>
    /// OpenAI direct (api.openai.com). Uses
    /// <c>Microsoft.Extensions.AI.OpenAI</c>'s adapter.
    /// <strong>Data residency:</strong> prompts cross the Atlantic.
    /// Weak choice for state-institution data; surfaced with a warning
    /// in the settings UI.
    /// </summary>
    OpenAI = 2,

    /// <summary>
    /// Azure OpenAI. Same adapter as <see cref="OpenAI"/>, but pointed
    /// at the Azure endpoint and authenticated with the Azure key. Best
    /// EU-data-residency story (Sweden Central, Switzerland North).
    /// Recommended for LAUS production usage.
    /// </summary>
    AzureOpenAI = 3,

    /// <summary>
    /// DeepSeek (api.deepseek.com), an OpenAI-compatible endpoint.
    /// <strong>Hosted in China.</strong> Surfaced with an explicit
    /// data-residency warning in the settings UI; not recommended for
    /// any prompt containing state-institution data.
    /// </summary>
    DeepSeek = 4,

    /// <summary>
    /// Generic OpenAI-compatible HTTP endpoint — Ollama, LM Studio, vLLM,
    /// any self-hosted inference server that speaks the OpenAI chat
    /// protocol. Operator supplies the base URL. Lets paranoid customers
    /// keep all prompts on-prem at the cost of model quality + latency.
    /// </summary>
    LocalOpenAiCompatible = 5,
}
