// The canonical IStepHandler now lives in KrakenDeploy.Contracts.Steps so step
// packages (Phase D-8) can reference the SDK surface alone — no agent dep.
// This file is kept ONLY as a namespace re-export so existing in-DI handlers
// in the agent (KrakenIis, OctopusTentaclePackage, Script, etc.) keep compiling
// without changing every using directive. Once Phase D-8 has extracted every
// built-in into its own package this file can go away.
global using IStepHandler        = KrakenDeploy.Contracts.Steps.IStepHandler;
global using StepHandlerContext  = KrakenDeploy.Contracts.Steps.StepHandlerContext;
