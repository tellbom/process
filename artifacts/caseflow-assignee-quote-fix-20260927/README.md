# CaseFlow assignee quote fix (2026-09-27)

This package contains only the files for the Flowable JSON-array / assignee quote fix.

Copy each tree into its matching project root:

- `process-v3/` -> the Process Center V3 source root (local source: `E:\process\process-v3`)
- `process-v4/` -> the Process Center V4 source root (local source: `E:\process\process`)
- `caseflow/` -> the CaseFlow source root (local source: `E:\Web\caseflow`)

V3 and V4 are separate versions. Replace only the tree you actually deploy. Back up intranet-local changes in these six paths before replacing them.

The two C# files in each version send Flowable collection variables as JSON arrays. The two BPMN files use `${assignee.asText()}` at multi-instance tasks so an assignee is the employee ID without literal quote characters.

The separate `ProcessLifecycleAppService.cs` change that injects `starterAssignee` from the JWT is intentionally excluded: it changes the start contract and is not required for this quote fix. Keep the intranet's existing first-node assignment contract.

After copying C# source files, build and publish the selected Process Center version, then restart that deployed service. Replacing a BPMN file on disk does not update an existing Flowable deployment; redeploy each changed BPMN with its existing matching `.slot.json`. New process instances must use the newly deployed definitions. Existing instances do not migrate.

No configuration files, credentials, binaries, or unrelated dirty files are included. See SHA256SUMS.txt for file hashes.
