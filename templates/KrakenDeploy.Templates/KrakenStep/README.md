# KrakenStep

A KrakenDeploy step package scaffolded from `dotnet new krakenstep`.

## Layout

```
KrakenStep/
├── KrakenStep.csproj      ← references Kraken.SDK + carries the pack target
├── SampleStepHandler.cs   ← your IStepHandler implementation
├── ui-schema.json         ← the form schema the KrakenDeploy editor renders
└── README.md              ← this file
```

## Build

```pwsh
dotnet build
```

Produces `bin/Debug/net10.0/PACKAGE_ID_PLACEHOLDER-1.0.0.kdeploy-step` — that file is
the deployable artifact. Upload it through the KrakenDeploy admin UI
(`/step-packages` → Upload) or POST to `/api/step-packages` with
`Permission.StepPackageManage`.

## Next steps

1. Replace `STEP_TYPE_PLACEHOLDER` in `KrakenStep.csproj`,
   `SampleStepHandler.cs`, and `ui-schema.json` with your step type id.
2. Update `PACKAGE_ID_PLACEHOLDER` in `KrakenStep.csproj` and
   `ui-schema.json` to your package id (lower-case, dotted —
   `mycompany.feature-name`).
3. Bump `KrakenStepPackageVersion` for each release.
4. Replace `SampleStepHandler.HandleAsync` with the real deployment logic.
5. Extend `ui-schema.json` with the form fields your handler reads from
   `context.Step.Config`.

See [docs/sdk-surface.md](https://github.com/your-org/KrakenDeploy/blob/main/docs/sdk-surface.md)
for the stable Kraken.SDK API contract.
