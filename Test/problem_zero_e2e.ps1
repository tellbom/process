param(
    [string]$BaseUrl = 'http://192.168.124.5:5012',
    [string]$EvidenceDir = 'docs/test-reports/evidence/problem-zero'
)

$ErrorActionPreference = 'Stop'
$EvidenceDir = [IO.Path]::GetFullPath((Join-Path (Get-Location) $EvidenceDir))
New-Item -ItemType Directory -Force $EvidenceDir | Out-Null

function ConvertTo-Base64Url([string]$Value) {
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-TestJwt([string]$UserId) {
    $header = ConvertTo-Base64Url '{"alg":"none","typ":"JWT"}'
    $payload = ConvertTo-Base64Url ((@{ userid = $UserId; sub = $UserId } | ConvertTo-Json -Compress))
    "$header.$payload."
}

function Invoke-Api {
    param([string]$Method, [string]$Path, [string]$UserId, $Body)
    $parameters = @{
        Method = $Method
        Uri = "$BaseUrl$Path"
        Headers = @{ Authorization = "Bearer $(New-TestJwt $UserId)" }
        ContentType = 'application/json; charset=utf-8'
    }
    if ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress }
    Invoke-RestMethod @parameters
}

function Save-Json([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -Encoding utf8 (Join-Path $EvidenceDir $Name)
}

function Complete-Node {
    param(
        [string]$BusinessId,
        [string]$ExpectedNode,
        [string]$EmployeeId,
        [array]$Selections = @(),
        [hashtable]$Variables = @{}
    )
    $progress = Invoke-Api GET "/api/processes/$BusinessId/progress" $EmployeeId $null
    $activeNode = @($progress.data.currentNodes | Where-Object { $_.nodeId -eq $ExpectedNode })
    if ($activeNode.Count -ne 1) { throw "Expected one active $ExpectedNode node for $BusinessId, found $($activeNode.Count)." }
    $pending = Invoke-Api GET "/api/tasks/pending?employeeId=$EmployeeId&businessType=problem_zero&pageIndex=1&pageSize=100" $EmployeeId $null
    $task = @($pending.data.items | Where-Object { $_.businessId -eq $BusinessId -and $_.taskId -eq $activeNode[0].taskId })
    if ($task.Count -ne 1) { throw "Expected task $($activeNode[0].taskId) in pending work for $BusinessId/$EmployeeId, found $($task.Count)." }
    $response = Invoke-Api POST '/api/tasks/complete' $EmployeeId @{
        businessId = $BusinessId
        taskId = $task[0].taskId
        employeeId = $EmployeeId
        action = 1
        comment = "E2E approve $ExpectedNode"
        nextSlotSelections = $Selections
        businessVariables = $Variables
    }
    if (-not $response.success) { throw "Complete failed for $BusinessId/${ExpectedNode}: $($response.message)" }
    $response
}

function Start-Scenario([string]$BusinessId) {
    $roles = @(
        @{ roleKey = 'problem_zero_team_leader'; mode = 'multiple'; users = @('PZ_TEAM') },
        @{ roleKey = 'problem_zero_quality_member'; mode = 'multiple'; users = @('PZ_QUALITY') },
        @{ roleKey = 'problem_zero_responsible_person'; mode = 'multiple'; users = @('PZ_RESP') },
        @{ roleKey = 'problem_zero_counterpart_leader'; mode = 'multiple'; users = @('PZ_COUNTER') },
        @{ roleKey = 'problem_zero_discoverer'; mode = 'multiple'; users = @('PZ_DISC') }
    )
    $response = Invoke-Api POST '/api/processes/start' 'PZ_START' @{
        businessType = 'problem_zero'
        businessId = $BusinessId
        initialSlotSelections = @(@{ slotKey = 'team_leader'; users = @('PZ_TEAM') })
        assigneeContract = @{ roles = $roles }
        businessVariables = @{ starterAssignee = 'PZ_START' }
        callback = @{ url = "$BaseUrl/api/test/process-callback"; timeoutSeconds = 30; retryCount = 1 }
    }
    if (-not $response.success) { throw "Start failed for ${BusinessId}: $($response.message)" }
    $response
}

function Wait-Completed([string]$BusinessId) {
    for ($i = 0; $i -lt 20; $i++) {
        $status = Invoke-Api GET "/api/processes/$BusinessId/status" 'PZ_START' $null
        if ($status.data.status -eq 'completed') { return $status }
        Start-Sleep -Milliseconds 500
    }
    throw "Process $BusinessId did not reach completed status."
}

function Save-ScenarioEvidence([string]$Name, [string]$BusinessId) {
    Save-Json "$Name-status.json" (Wait-Completed $BusinessId)
    Save-Json "$Name-progress.json" (Invoke-Api GET "/api/processes/$BusinessId/progress" 'PZ_START' $null)
    Save-Json "$Name-audit-history.json" (Invoke-Api GET "/api/processes/$BusinessId/audit-history" 'PZ_START' $null)
    Save-Json "$Name-flow-render.json" (Invoke-Api GET "/api/processes/$BusinessId/flow-render" 'PZ_START' $null)
    $callbacks = Invoke-Api GET "/api/test/callbacks?businessId=$BusinessId" 'PZ_START' $null
    Save-Json "$Name-callbacks.json" $callbacks
    $callbacks
}

$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$ids = [ordered]@{
    solved = "PZ_E2E_SOLVED_$stamp"
    special = "PZ_E2E_SPECIAL_$stamp"
    nonspecial = "PZ_E2E_NONSPECIAL_$stamp"
}
Save-Json 'scenario-ids.json' $ids

Save-Json 'solved-start.json' (Start-Scenario $ids.solved)
Complete-Node $ids.solved 'ut01_starter_submit' 'PZ_START' @(@{ slotKey = 'team_leader'; users = @('PZ_TEAM') }) | Out-Null
Complete-Node $ids.solved 'ut02_team_leader_confirm' 'PZ_TEAM' @(@{ slotKey = 'quality_group'; users = @('PZ_QUALITY') }) | Out-Null
Complete-Node $ids.solved 'ut03_quality_group_confirm' 'PZ_QUALITY' @(@{ slotKey = 'direct_end_when_solved'; users = @() }) @{ IS_SOLVED = $true } | Out-Null
$solvedCallbacks = Save-ScenarioEvidence 'solved' $ids.solved

Save-Json 'special-start.json' (Start-Scenario $ids.special)
Complete-Node $ids.special 'ut01_starter_submit' 'PZ_START' @(@{ slotKey = 'team_leader'; users = @('PZ_TEAM') }) | Out-Null
Complete-Node $ids.special 'ut02_team_leader_confirm' 'PZ_TEAM' @(@{ slotKey = 'quality_group'; users = @('PZ_QUALITY') }) | Out-Null
Complete-Node $ids.special 'ut03_quality_group_confirm' 'PZ_QUALITY' @(@{ slotKey = 'responsible_person'; users = @('PZ_RESP') }) @{ IS_SOLVED = $false } | Out-Null
Complete-Node $ids.special 'ut04_responsible_confirm' 'PZ_RESP' @(@{ slotKey = 'counterpart_leader'; users = @('PZ_COUNTER') }) @{ PROBLEM_ATTRIBUTE = $true } | Out-Null
Complete-Node $ids.special 'ut05_counterpart_leader_review' 'PZ_COUNTER' @(@{ slotKey = 'discoverer_after_counterpart'; users = @('PZ_DISC') }) | Out-Null
Complete-Node $ids.special 'ut06_discoverer_confirm' 'PZ_DISC' | Out-Null
$specialCallbacks = Save-ScenarioEvidence 'special' $ids.special

Save-Json 'nonspecial-start.json' (Start-Scenario $ids.nonspecial)
Complete-Node $ids.nonspecial 'ut01_starter_submit' 'PZ_START' @(@{ slotKey = 'team_leader'; users = @('PZ_TEAM') }) | Out-Null
Complete-Node $ids.nonspecial 'ut02_team_leader_confirm' 'PZ_TEAM' @(@{ slotKey = 'quality_group'; users = @('PZ_QUALITY') }) | Out-Null
Complete-Node $ids.nonspecial 'ut03_quality_group_confirm' 'PZ_QUALITY' @(@{ slotKey = 'responsible_person'; users = @('PZ_RESP') }) @{ IS_SOLVED = $false } | Out-Null
Complete-Node $ids.nonspecial 'ut04_responsible_confirm' 'PZ_RESP' @(@{ slotKey = 'discoverer_direct'; users = @('PZ_DISC') }) @{ PROBLEM_ATTRIBUTE = $false } | Out-Null
Complete-Node $ids.nonspecial 'ut06_discoverer_confirm' 'PZ_DISC' | Out-Null
$nonspecialCallbacks = Save-ScenarioEvidence 'nonspecial' $ids.nonspecial

$summary = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    baseUrl = $BaseUrl
    scenarios = @(
        @{ name = 'solved'; businessId = $ids.solved; expectedNodes = 3; actualCallbacks = $solvedCallbacks.count },
        @{ name = 'special'; businessId = $ids.special; expectedNodes = 6; actualCallbacks = $specialCallbacks.count },
        @{ name = 'nonspecial'; businessId = $ids.nonspecial; expectedNodes = 5; actualCallbacks = $nonspecialCallbacks.count }
    )
}
Save-Json 'run-summary.json' $summary
$summary | ConvertTo-Json -Depth 10
