# Task: Add Data Scientist Persona and Python/Report Generation Capability

- [x] Update `ICapability` interface and `CapabilityRegistry` to support startup verification (`ValidateSystemPrerequisites` / `ValidateAll`).
- [x] Implement `ValidateSystemPrerequisites()` in existing capabilities as no-ops:
  - [x] `CodeCapability.cs`
  - [x] `GitHubCapability.cs`
  - [x] `JiraCapability.cs`
  - [x] `KibanaCapability.cs`
- [x] Create `DataScienceCapability.cs` with startup verification (`python`/`python3` resolution and pandas/matplotlib/reportlab checking) and `run_python` tool.
- [x] Register `data_scientist` persona in `Personas.cs`.
- [x] Update `Orchestrator.cs` file ingestion and context tracking:
  - [x] Copy attachments to conversation file area immediately in `HandleMessageAsync`.
  - [x] Update `AttachmentNote` format.
  - [x] Implement `FilesNote` to list active files in the conversation's files area.
  - [x] Inject `FilesNote` into `dynamicContext`.
  - [x] Update `delegate` tool parameter description to mention `data_scientist`.
- [x] Wire up capability registry and persona store in API `Program.cs` and run startup verification.
- [x] Wire up DataScienceCapability in Slack `Program.cs` and run startup verification.
- [x] Update Slack orchestrator system prompt in `SlackPrompts.cs`.
- [ ] Verify execution by building the solution and executing python scripts.
