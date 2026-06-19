# Agent Instructions

## API Documentation Requirement

Before writing or changing any Atera API integration code, Atera API DTOs, request/response models, pagination logic, authentication handling, or tests that assert Atera wire shapes, consult the official Atera API documentation:

- https://app.atera.com/apidocs

Before writing or changing any Snipe-IT API integration code, Snipe-IT API DTOs, request/response models, create/update/search payloads, error handling, or tests that assert Snipe-IT wire shapes, consult the official Snipe-IT API documentation:

- https://snipe-it.readme.io/reference/api-overview

Do not invent API field names, endpoint paths, request payloads, response payloads, pagination behavior, status/error semantics, or authentication headers for either system. If documentation is unavailable or ambiguous, stop and record the uncertainty instead of guessing.

The initial Core DTOs may be scaffold-level contracts based on the project master plan. Treat them as provisional until verified against the official API documentation during the relevant Atera Pull or Snipe-IT Import implementation phase.

## Real API Testing Policy

Automated tests must never call the real Atera or Snipe-IT APIs. Unit, contract, integration-style, and CI tests must use mocked HTTP handlers, fake clients, local fixtures, or sanitized sample payloads.

Do not add Python probes for Atera API verification. If a real API key must be used to validate behavior, that validation must be a manual-only run by the project owner/operator.

Any manual real-key verification must be documented with:

- exact commands or UI steps to run
- required local-only configuration or environment variables
- confirmation that the API key must not be printed, logged, committed, or stored in tracked files
- expected safe/sanitized output
- cleanup steps for removing session environment variables or temporary files

Manual real-key verification must stay outside normal `dotnet test`, build, and CI workflows.

## Technical Specification Documents

Technical specification documents under `docs/technical-specs/` are implementation blueprints for later AI coding agents. They must be specific enough for an agent to generate code directly without making design decisions.

Each technical spec should clearly define:

- The concrete classes, interfaces, and namespaces to create or change.
- Public properties, public methods, method signatures, and expected return types.
- Class responsibilities and how classes call or depend on each other.
- Input/output models, validation rules, error/warning behavior, and logging expectations.
- Required test cases and acceptance criteria.

Do not use technical specs as broad brainstorming documents. If a decision is still uncertain, write the uncertainty explicitly instead of hiding it behind vague language.

## Code Commenting Requirement

Every class must include a concise comment that explains its role, responsibility boundary, and how it fits into the module.

Key functions and methods must include comments that explain their purpose, important inputs and outputs, side effects, validation behavior, and failure behavior when those details are not obvious from the signature.

Comments should clarify intent and operational behavior. Do not add redundant comments that merely restate the code line-by-line.

## Module Development Workflow

Every module must be developed in this order:

1. Write `docs/module-plans/<module>-功能职责.md`
2. Write `docs/technical-specs/<module>-技术规格.md`
3. Generate production code and unit tests
4. Write `docs/test-guides/<module>-单元测试指导手册.md`

Do not skip directly to technical specs or code. The 功能职责 document defines the module boundary first: goal, inputs, outputs, external interface, success conditions, failure conditions, responsibilities excluded from the module, and extension points.

The 技术规格 document must be based on the approved 功能职责 document. It should then add concrete class/interface design, method signatures, public members, data flow, class calling relationships, validation rules, warning/error behavior, and required test cases.

Production code must be generated only after both the 功能职责 and 技术规格 documents exist for that module. The unit test guide is written after the tests exist, so it can describe the actual test commands, test coverage, mocking strategy, and common failure causes.

## Progress Documentation Requirement

Every time production code or automated tests are added or changed, update `progress.md` in the same work session.

The progress update must record:

- what code or tests changed
- what documentation changed
- what verification commands were run
- the latest known build/test result
- any remaining module gaps or next steps

Do not consider a coding task complete until `progress.md` reflects the new state.
