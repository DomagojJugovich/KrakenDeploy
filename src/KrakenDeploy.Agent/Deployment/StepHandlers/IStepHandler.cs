// The canonical IStepHandler now lives in KrakenDeploy.Contracts.Steps so step
// packages (Phase D-8) can reference the SDK surface alone — no agent dep.
// This file is kept ONLY as a namespace re-export so the two remaining in-DI
// handlers (SubstituteVariablesStepHandler, ManualInterventionStepHandler)
// keep compiling without changing every using directive. Phase D-8.9 retires
// those last two and this file can go away.
global using IStepHandler        = KrakenDeploy.Contracts.Steps.IStepHandler;
global using StepHandlerContext  = KrakenDeploy.Contracts.Steps.StepHandlerContext;
