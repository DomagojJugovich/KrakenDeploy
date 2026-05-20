// IStepHandler + StepHandlerContext moved to KrakenDeploy.Contracts.Steps as
// part of Phase D-8 (built-ins as packages). This global import keeps existing
// tests that reference the unqualified type names building without churn.
global using KrakenDeploy.Contracts.Steps;
