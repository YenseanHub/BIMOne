# BIMOne

[中文版本](README.zh-CN.md)

BIMOne is an **AI-assisted CAD-to-Revit reverse modeling toolchain** for building BIM workflows. It parses DWG/DXF drawings into structured data, infers architectural semantics such as walls and floors without project-specific layer rules, optionally cross-validates results against embedded DWG preview images, and finally drives a Revit 2025 add-in to generate LOD100 placeholder models and review reports.

> Core principle: **data correctness comes before modeling speed**. Every inference must be auditable, and low-confidence results must be escalated to human review.

## Key Features

- **Decoupled endpoints**: The CAD reader and the Revit modeler never communicate directly or share a process. They exchange data only through local JSON files, and every stage can be rerun independently.
- **CAD reading and diagnostics**: Uses `ACadSharp` to parse DWG/DXF files, extracting entity types, layers, coordinates, text, closure information and Z values, together with statistical summaries.
- **Rule-free semantic inference**: Combines layer-name keywords, geometric features and confidence scores to infer walls, floors, doors, windows, columns and other categories, while flagging items that need review.
- **Image cross-validation**: Compares parsed geometry with the embedded DWG preview using per-region IoU and reports low-consistency areas as targeted review clues, not as final verdicts.
- **Revit modeling and review**: Drives the Revit add-in through MCP tools, creates wall/floor placeholder elements inside a Transaction, and writes back element counts and issue lists.
- **Files as contracts**: Intermediate artifacts remain readable, editable and diffable, making them suitable for both LLM reasoning and manual correction.

## System Architecture

```text
DWG / DXF drawing
      │
      ▼
[1] cad__parse_cad                         → <task>.ir.json
      │                                      (entities, layers, coordinates, closure, Z values)
      ▼
[2] LLM reasoning + cad__verify_image       → <task>.verify.json
      │                                      (IoU, coverage, low-consistency regions; optional)
      ▼
[3] Write modeling commands                 → <task>.commands.json
      │                                      (core handoff artifact, directly editable)
      ▼
[4] revit__fanmo_build(dataPath=...)
      │   RevitModeler.McpServer → TCP 8982 → Plugin → CommandSet
      ▼
[5] revit__fanmo_review                     → <task>.review.json
      │                                      (wall/floor counts, issue list)
      ▼
Human review and fallback
```

The CAD side only reads and validates; the Revit side only models and reviews. The only coupling point between them is JSON files on disk.

## Repository Layout

```text
BIMOne/
├── cad-reader/                         # CAD reader (.NET 8)
│   ├── CadReader.sln
│   └── src/
│       ├── CadReader.Core/             # DWG/DXF parsing, IR, embedded preview verification
│       ├── CadReader.Cli/              # CLI entry point for manual checks and batch runs
│       └── CadReader.McpServer/        # stdio MCP Server: cad__* tools
├── revit-modeler/                      # Revit modeler (independent three-tier stack)
│   ├── RevitModeler.sln
│   ├── command.json                    # Command manifest: fanmo_build / fanmo_review
│   ├── docs/architecture-alignment.md
│   ├── scripts/deploy.ps1              # Revit add-in deployment script
│   ├── src/
│   │   ├── RevitModeler.McpServer/     # Node/stdio MCP Server: revit__* tools
│   │   ├── RevitModeler.Core/          # Pure logic layer, no RevitAPI dependency, unit-testable
│   │   ├── RevitModeler.Plugin/        # Revit add-in: Ribbon + TCP JSON-RPC host
│   │   └── RevitModeler.CommandSet/    # Revit API commands: modeling and review
│   └── tests/RevitModeler.Tests/       # xUnit tests
├── experts/bim-fanmo-master/           # Source package of the WorkBuddy expert
│   ├── agents/
│   ├── skills/fanmo-workflow/
│   └── .codebuddy-plugin/plugin.json
├── sampleData/一居室公寓.dwg             # Sample drawing
├── scripts/sync-expert.ps1             # Expert package sync script
├── work/                               # Default task workspace
├── CONTRACTS.md                        # Single index of endpoints and contracts
├── DATA-LAYER.md                       # Shared data-layer contract
├── README.md                           # English README
└── README.zh-CN.md                     # Chinese README
```

There is no single root solution; `cad-reader/` and `revit-modeler/` are two independent .NET solutions.

## Prerequisites

| Component | Requirement |
| --- | --- |
| Operating system | Windows (the Revit add-in is x64) |
| .NET SDK | .NET 8 |
| Node.js | >= 20 |
| Revit | 2025 or later (only required for modeling and deployment) |
| PowerShell | 5.1 or later |
| MCP client | For example WorkBuddy, used to load the two stdio MCP servers |

Key dependency versions:

- `ACadSharp 3.8.35` for DWG/DXF parsing.
- `Nice3point.Revit.Api.* 2025.*` and `RevitMCPSDK 2025.0.0` for the Revit 2025 add-in and command set.
- `@modelcontextprotocol/sdk ^1.26.0` and `zod ^3.24.2` for the Revit MCP Server.

## Quick Start

Run the following commands from the repository root using PowerShell.

### 1. Build and test

```powershell
# CAD reader
dotnet build cad-reader/CadReader.sln -c Release

# Revit modeler
dotnet build revit-modeler/RevitModeler.sln -c Release
dotnet test  revit-modeler/RevitModeler.sln -c Release

# Revit MCP Server
cd revit-modeler/src/RevitModeler.McpServer
npm install
npm run build
node smoke-test.mjs
node smoke-file-handoff.mjs
cd ../../..
```

### 2. Parse the sample drawing with the CLI

```powershell
dotnet run --project cad-reader/src/CadReader.Cli -c Release -- `
  sampleData/一居室公寓.dwg work/demo/apartment.ir.json `
  --commands work/demo/apartment.commands.json
```

The CLI writes the full IR to `outPath`. With `--summary`, `outPath` contains only the statistics summary. Use `--commands <path>` to generate the complete Revit command file from the same IR, including inferred wall thickness, floor outline and family placeholders.

### 3. Deploy the Revit add-in

```powershell
powershell -ExecutionPolicy Bypass -File revit-modeler/scripts/deploy.ps1 `
  -RevitVersion 2025 -Configuration Release
```

The script builds the plugin and command set, then copies them to:

```text
%APPDATA%\Autodesk\Revit\Addins\2025\
├── BIMOne.RevitModeler.addin
└── BIMOne.RevitModeler\
    ├── RevitModeler.Plugin.dll and dependencies
    └── Commands\RevitModeler\
        ├── RevitModeler.CommandSet.dll, RevitModeler.Core.dll and dependencies
        └── command.json
```

After starting Revit, open the **BIMOne Reverse Modeling** ribbon tab and click the modeling service on/off button.

### 4. Configure the MCP servers

Both MCP servers must be registered with your MCP client as stdio servers. They are not yet present in `~/.workbuddy/mcp.json`; add them using the format expected by your client:

| Server | Purpose | Example launch command |
| --- | --- | --- |
| `CadReader.McpServer` | `cad__*` tools | `dotnet <repo>\cad-reader\src\CadReader.McpServer\bin\Release\net8.0\CadReader.McpServer.dll` |
| `RevitModeler.McpServer` | `revit__*` tools | `node <repo>\revit-modeler\src\RevitModeler.McpServer\build\index.js` |

Replace `<repo>` with the absolute path to the repository root.

### 5. Sync the WorkBuddy expert package

`experts/bim-fanmo-master/` is the source of truth. After changing the expert, you must sync it; otherwise WorkBuddy will keep loading the old version:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/sync-expert.ps1
powershell -ExecutionPolicy Bypass -File scripts/sync-expert.ps1 -Zip
```

The sync overwrites files with the same name in the installation directory but does not delete extra files there. `.codebuddy-plugin/plugin.json` is synced as well.

## MCP Tools

| Tool | Input | Output | Purpose |
| --- | --- | --- | --- |
| `cad__parse_cad` | `path`; optional `summaryOnly`, `outPath` | IR JSON or "path + summary" | Parse DWG/DXF; passing `outPath` is recommended |
| `cad__verify_image` | `path`; optional `irPath`, `outPath` | IoU, coverage, precision, low-consistency regions | Cross-check against the embedded DWG preview |
| `revit__ping` | None | Service status and registered commands | Connectivity check |
| `revit__fanmo_build` | `dataPath` (recommended) or `data` | Created Revit ElementIds | Batch modeling; currently walls and floors at LOD100 |
| `revit__fanmo_review` | Optional `minWallLengthMm`, `outPath` | Review report | Read-only scan of walls/floors with issue list |

## Data Handoff Contract

Use a separate subdirectory per task under `work/<task-name>/`:

```text
work/<task-name>/
├── <task>.ir.json          # written by cad__parse_cad: structured entity data
├── <task>.verify.json      # written by cad__verify_image: image cross-validation report, optional
├── <task>.commands.json    # written by LLM reasoning: modeling commands, core handoff artifact
└── <task>.review.json      # written by revit__fanmo_review: review report
```

Key conventions:

- All lengths are in **millimeters**.
- `start`, `end` and `points` in `commands.json` must come from the real coordinates in the IR. Never invent them, otherwise walls will pile up at the origin.
- All file parameters are optional. Without file output, tools keep returning results inline for backward compatibility.
- Files are stateless snapshots: neither endpoint maintains a session, and any stage can be rerun independently.

See `DATA-LAYER.md` for field definitions and `CONTRACTS.md` for the endpoint map.

## Configuration

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `BIMONE_REVIT_PORT` | `8982` | Revit add-in listening port and MCP client connection port; both sides must match |
| `BIMONE_REVIT_HOST` | `127.0.0.1` | Host used by the MCP client to reach the Revit add-in; client side only |
| `BIMONE_WORKDIR` | `BIMOne/work/` | Documented workspace override; not wired into the code yet, do not rely on it |

## Validation Status and Known Limitations

Currently verified:

- CAD side: real DWG parsing and `outPath` file handoff smoke test.
- Revit side: .NET projects compile and all 12 pure-logic tests pass.
- Revit MCP Server: `tsc` build, basic link smoke test and file-handoff smoke test pass.

Current limitations:

- **Not validated inside Revit**: `ExternalEvent`, UI-thread switching and `Transaction` modeling must be tested on a machine with Revit 2025. Until then, do not claim that modeling has been verified end to end.
- **Neither MCP server has been added to `~/.workbuddy/mcp.json`**, so WorkBuddy will not load the tools by default.
- Only wall/floor LOD100 placeholder modeling is implemented; family refinement for doors, windows and columns (LOD200+) is not available yet.
- Review currently checks only zero-length walls and element counts; collision, closure and missing-element checks are planned for a later stage.
- Image validation only works with DWG files that contain an embedded preview; DXF has no such capability. The preview contains only layers visible at save time, and a low IoU does not necessarily mean an error. Treat it as a review clue only.
- Before reverse modeling a plan, confirm that Z values are zero. Splines must be approximated before modeling, and drawings with chaotic layer names require semantic inference plus human review.
- `BIMONE_WORKDIR` exists only as a documented convention; the current implementation does not read it.

## Documentation Index

- `CONTRACTS.md`: Single index of MCP tools, handoff files and Revit commands.
- `DATA-LAYER.md`: Field contracts for the four JSON files and the end-to-end data flow.
- `revit-modeler/README.md`: Revit three-tier structure, build, deployment and limitations.
- `revit-modeler/docs/architecture-alignment.md`: Architectural alignment of the Revit side.
- `experts/bim-fanmo-master/README.md`: Expert package installation, sync and usage examples.
- `experts/bim-fanmo-master/agents/bim-fanmo-master.md`: Expert capabilities and the Phase 0-5 workflow.
- `experts/bim-fanmo-master/skills/fanmo-workflow/SKILL.md`: Semantic inference thresholds, image interpretation and output templates.
- `README.zh-CN.md`: Chinese version of this README.

## License and Maintenance

- Maintainer: Yensean
- Contact email: `yenseaon@foxmail.com`
- Licensed under the MIT License. See `LICENSE`.
