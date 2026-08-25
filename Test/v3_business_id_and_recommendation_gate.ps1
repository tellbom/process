param(
    [string]$BaseUrl = 'http://127.0.0.1:5012'
)

$ErrorActionPreference = 'Stop'

function ConvertTo-Base64Url([string]$Value) {
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-TestJwt([string]$UserId) {
    $header = ConvertTo-Base64Url '{"alg":"none","typ":"JWT"}'
    $payload = ConvertTo-Base64Url ((@{ userid = $UserId; sub = $UserId } | ConvertTo-Json -Compress))
    "$header.$payload."
}

function Invoke-Api {
    param([string]$Method, [string]$Path, [string]$UserId, $Body, [int[]]$ExpectedStatus = @(200))

    $parameters = @{
        Method = $Method
        Uri = "$BaseUrl$Path"
        Headers = @{ Authorization = "Bearer $(New-TestJwt $UserId)" }
        ContentType = 'application/json; charset=utf-8'
    }
    if ($null -ne $Body) {
        $parameters.Body = $Body | ConvertTo-Json -Depth 30 -Compress
    }

    try {
        $response = Invoke-WebRequest @parameters -UseBasicParsing
        $status = [int]$response.StatusCode
        $content = $response.Content
    }
    catch {
        if ($null -eq $_.Exception.Response) { throw }
        $status = [int]$_.Exception.Response.StatusCode
        $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
        $content = $reader.ReadToEnd()
        $reader.Dispose()
    }

    if ($ExpectedStatus -notcontains $status) {
        throw "$Method $Path returned HTTP $status, expected $($ExpectedStatus -join ','): $content"
    }

    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    return $content | ConvertFrom-Json
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$roles = @(
    @{ roleKey = 'group_leader'; mode = 'single'; users = @('GATE_GL') },
    @{ roleKey = 'inspection_office_reviewer'; mode = 'single'; users = @('GATE_IO') },
    @{ roleKey = 'integrity_dept_reviewer'; mode = 'single'; users = @('GATE_ID') },
    @{ roleKey = 'integrity_head'; mode = 'single'; users = @('GATE_IH') },
    @{ roleKey = 'feedback_person'; mode = 'single'; users = @('GATE_FB') },
    @{ roleKey = 'office_director'; mode = 'single'; users = @('GATE_OD') },
    @{ roleKey = 'secretary'; mode = 'single'; users = @('GATE_SEC') }
)

$descriptions = @(
    @{ roleKey = 'starter'; description = 'Review and submit this personnel selection request' },
    @{ roleKey = 'group_leader'; description = 'Confirm this personnel selection arrangement' },
    @{ roleKey = 'inspection_office_reviewer'; description = 'Review the selection package for the inspection office' },
    @{ roleKey = 'integrity_dept_reviewer'; description = 'Review the integrity package' },
    @{ roleKey = 'integrity_head'; description = 'Choose the next handling path' },
    @{ roleKey = 'office_director'; description = 'Review the handling result' },
    @{ roleKey = 'secretary'; description = 'Perform final approval' }
)

function Start-Process([string]$BusinessId, [string]$Starter, [string]$Title) {
    Invoke-Api POST '/api/processes/start' $Starter @{
        businessType = 'personnel_selection_approval'
        businessId = $BusinessId
        businessTitle = $Title
        initialSlotSelections = @(@{ slotKey = 'group_leader'; users = @('GATE_GL') })
        assigneeContract = @{ roles = $roles; nodeDescriptions = $descriptions }
        businessVariables = @{ starterAssignee = $Starter }
        callback = @{ url = "$BaseUrl/api/test/process-callback"; timeoutSeconds = 30; retryCount = 1 }
    }
}

function Get-PendingTask([string]$BusinessId, [string]$EmployeeId) {
    $response = Invoke-Api GET "/api/tasks/pending?employeeId=$EmployeeId&businessType=personnel_selection_approval&pageIndex=1&pageSize=100" $EmployeeId $null
    $tasks = @($response.data.items | Where-Object { $_.businessId -eq $BusinessId })
    Assert-True ($tasks.Count -eq 1) "Expected exactly one pending task for $BusinessId/$EmployeeId, found $($tasks.Count)."
    return $tasks[0]
}

function Complete-Task([string]$BusinessId, [string]$EmployeeId, [string]$TaskId, [array]$Selections = @(), [hashtable]$Variables = @{}) {
    Invoke-Api POST '/api/tasks/complete' $EmployeeId @{
        businessId = $BusinessId
        taskId = $TaskId
        employeeId = $EmployeeId
        action = 1
        comment = 'V3 API gate approval'
        nextSlotSelections = $Selections
        businessVariables = $Variables
    }
}

$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$reuseBusinessId = "V3_GATE_REUSE_$stamp"
$fullBusinessId = "V3_GATE_FULL_$stamp"

$reuseStart = Start-Process $reuseBusinessId 'GATE_START_A' 'V3 reuse prevention gate'
Assert-True ($reuseStart.success -eq $true) 'Initial reuse scenario start failed.'
$starterTask = Get-PendingTask $reuseBusinessId 'GATE_START_A'
Assert-True ($starterTask.nodeDescription -eq 'Review and submit this personnel selection request') 'Pending nodeDescription is incorrect.'
Assert-True ($starterTask.actionDescription -eq $starterTask.nodeDescription) 'Pending actionDescription did not prefer nodeDescription.'
Assert-True ($starterTask.businessDisplayName -eq 'V3 reuse prevention gate') 'Pending business display name is incorrect.'
Assert-True (-not [string]::IsNullOrWhiteSpace($starterTask.processInstanceId)) 'Pending processInstanceId is missing.'
Assert-True (-not [string]::IsNullOrWhiteSpace($starterTask.taskDefinitionKey)) 'Pending taskDefinitionKey is missing.'

$rangeResponse = Invoke-Api POST '/api/tasks/complete' 'GATE_START_A' @{
    businessId = $reuseBusinessId
    employeeId = 'GATE_START_A'
    action = 1
    nextSlotSelections = @(@{ slotKey = 'group_leader'; users = @('OUT_OF_RANGE') })
} @(400)
Assert-True ($rangeResponse.code -eq 'ASSIGNEE_OUT_OF_RECOMMENDED_RANGE') "Unexpected range error code: $($rangeResponse.code)"

$starterTaskAfterReject = Get-PendingTask $reuseBusinessId 'GATE_START_A'
Assert-True ($starterTaskAfterReject.taskId -eq $starterTask.taskId) 'Rejected out-of-range request changed the active task.'
Complete-Task $reuseBusinessId 'GATE_START_A' $starterTask.taskId @(@{ slotKey = 'group_leader'; users = @('GATE_GL') }) | Out-Null

$groupTask = Get-PendingTask $reuseBusinessId 'GATE_GL'
Complete-Task $reuseBusinessId 'GATE_GL' $groupTask.taskId @(@{ slotKey = 'inspection_office_reviewer'; users = @('GATE_IO') }) | Out-Null
$inspectionTask = Get-PendingTask $reuseBusinessId 'GATE_IO'
Complete-Task $reuseBusinessId 'GATE_IO' $inspectionTask.taskId @(@{ slotKey = 'integrity_dept_reviewer'; users = @('GATE_ID') }) | Out-Null

Invoke-Api POST '/api/processes/terminate' 'GATE_START_A' @{
    businessId = $reuseBusinessId
    reason = 'V3 businessId reuse regression gate'
} | Out-Null

$duplicateResponse = Invoke-Api POST '/api/processes/start' 'GATE_START_B' @{
    businessType = 'personnel_selection_approval'
    businessId = $reuseBusinessId
    businessTitle = 'This process must not start'
    initialSlotSelections = @(@{ slotKey = 'group_leader'; users = @('OTHER_GL') })
    assigneeContract = @{ roles = @(@{ roleKey = 'group_leader'; mode = 'single'; users = @('OTHER_GL') }) }
    businessVariables = @{ starterAssignee = 'GATE_START_B' }
    callback = @{ url = "$BaseUrl/api/test/process-callback"; timeoutSeconds = 30; retryCount = 1 }
} @(400)
Assert-True ($duplicateResponse.code -eq 'BUSINESS_ID_ALREADY_USED') "Unexpected duplicate start error code: $($duplicateResponse.code)"

$fullStart = Start-Process $fullBusinessId 'GATE_START_FULL' 'V3 complete automatic process gate'
Assert-True ($fullStart.success -eq $true) 'Full scenario start failed.'
$task = Get-PendingTask $fullBusinessId 'GATE_START_FULL'
Complete-Task $fullBusinessId 'GATE_START_FULL' $task.taskId @(@{ slotKey = 'group_leader'; users = @('GATE_GL') }) | Out-Null
$task = Get-PendingTask $fullBusinessId 'GATE_GL'
Complete-Task $fullBusinessId 'GATE_GL' $task.taskId @(@{ slotKey = 'inspection_office_reviewer'; users = @('GATE_IO') }) | Out-Null
$task = Get-PendingTask $fullBusinessId 'GATE_IO'
Complete-Task $fullBusinessId 'GATE_IO' $task.taskId @(@{ slotKey = 'integrity_dept_reviewer'; users = @('GATE_ID') }) | Out-Null
$task = Get-PendingTask $fullBusinessId 'GATE_ID'
Complete-Task $fullBusinessId 'GATE_ID' $task.taskId @(@{ slotKey = 'integrity_head'; users = @('GATE_IH') }) | Out-Null
$task = Get-PendingTask $fullBusinessId 'GATE_IH'
Complete-Task $fullBusinessId 'GATE_IH' $task.taskId @(@{ slotKey = 'office_director_self_handle'; users = @('GATE_OD') }) @{ needPersonFeedback = $false } | Out-Null
$task = Get-PendingTask $fullBusinessId 'GATE_OD'
Complete-Task $fullBusinessId 'GATE_OD' $task.taskId @(@{ slotKey = 'secretary'; users = @('GATE_SEC') }) | Out-Null
$task = Get-PendingTask $fullBusinessId 'GATE_SEC'
Complete-Task $fullBusinessId 'GATE_SEC' $task.taskId | Out-Null

$status = $null
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    $status = Invoke-Api GET "/api/processes/$fullBusinessId/status" 'GATE_START_FULL' $null
    if ($status.data.status -eq 'completed') { break }
    Start-Sleep -Milliseconds 500
}
Assert-True ($status.data.status -eq 'completed') "Full scenario did not complete; status=$($status.data.status)"
$progress = Invoke-Api GET "/api/processes/$fullBusinessId/progress" 'GATE_START_FULL' $null
Assert-True (@($progress.data.currentNodes).Count -eq 0) 'Completed process still has active nodes.'
Assert-True (@($progress.data.auditHistory).Count -eq 7) "Expected 7 audit records, found $(@($progress.data.auditHistory).Count)."

[ordered]@{
    passed = $true
    baseUrl = $BaseUrl
    reuseBusinessId = $reuseBusinessId
    reuseProcessInstanceId = $reuseStart.data.processInstanceId
    duplicateStartCode = $duplicateResponse.code
    fullBusinessId = $fullBusinessId
    fullProcessInstanceId = $fullStart.data.processInstanceId
    fullStatus = $status.data.status
    auditCount = @($progress.data.auditHistory).Count
    pendingBusinessFields = @(
        'businessTitle', 'businessDisplayName', 'nodeDescription', 'actionDescription',
        'processInstanceId', 'taskDefinitionKey', 'createdBy', 'processStatus', 'isOverdue'
    )
} | ConvertTo-Json -Depth 10
