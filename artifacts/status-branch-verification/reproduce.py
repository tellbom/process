"""Exercise real start, approval, Flowable HTTP callback, Worker delivery and list APIs.

Uses its own DM8 schema, ES indices, process key, business IDs and local callback sink.
The original appsettings.json and existing business rows/queues are never modified.
"""
import base64
import http.server
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent
SCHEMA = "REPRO_STATUS_6F42A819"
KEY = "repro_status_6f42a819"
USER = "REPRO_STATUS_6F42A819"
BASE = "http://127.0.0.1:5012"
received = []
results = {}


class Sink(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        payload = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))))
        received.append(payload)
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"success":true}')

    def log_message(self, *args):
        pass


def b64(value):
    return base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip("=")


TOKEN = b64({"alg": "none", "typ": "JWT"}) + "." + b64({"userid": USER, "sub": USER}) + "."


def api(method, path, body=None, content_type="application/json"):
    data = json.dumps(body).encode() if body is not None and not isinstance(body, bytes) else body
    request = urllib.request.Request(BASE + path, data=data, method=method, headers={
        "Authorization": "Bearer " + TOKEN, "Content-Type": content_type,
    })
    try:
        with urllib.request.urlopen(request, timeout=25) as response:
            value = json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"{method} {path}: {error.code} {error.read().decode()}") from error
    if isinstance(value, dict) and value.get("success") is False:
        raise RuntimeError(f"{method} {path}: {value}")
    return value.get("data", value)


def start_server(label, dll):
    env = os.environ.copy()
    env.update({
        "ASPNETCORE_URLS": "http://0.0.0.0:5012", "ASPNETCORE_ENVIRONMENT": "Development",
        "Dm8__Schema": SCHEMA, "CallbackWorker__Enabled": "true", "CallbackWorker__WorkerCount": "1",
        "CallbackWorker__PollIntervalMilliseconds": "100", "ProcessNotification__Enabled": "false",
        "Jwt__Mode": "TrustedJwt", "Flowable__FrameworkCallbackUrl": "http://192.168.124.5:5012/api/callback/flowable",
        "Readiness__Enabled": "true", "Readiness__RequireElasticSearch": "true",
        "ElasticSearch__IndexName": KEY + "-metadata", "ElasticSearch__AuditIndexName": KEY + "-audits",
        "ElasticSearch__SemanticIndexName": KEY + "-semantics",
    })
    log = open(OUT / f"{label}-server.log", "w", encoding="utf-8")
    server = subprocess.Popen(["dotnet", str(dll)], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    try:
        for _ in range(100):
            if server.poll() is not None:
                raise RuntimeError(f"{label} server exited; see server log")
            try:
                if api("GET", "/health/ready").get("status") == "ready":
                    print(f"{label}: isolated backend ready", flush=True)
                    return server, log
            except Exception:
                pass
            time.sleep(.3)
        raise RuntimeError(f"{label} server was not ready")
    except Exception:
        server.terminate()
        server.wait(timeout=10)
        log.close()
        raise


def deploy():
    xml = '''<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:flowable="http://flowable.org/bpmn" targetNamespace="urn:status-repro">
<process id="KEY" name="Isolated status reproduction" isExecutable="true">
  <startEvent id="start"/>
  <userTask id="review" name="Choose next path" flowable:assignee="${starterAssignee}"/>
  <exclusiveGateway id="decision"/>
  <userTask id="nextReview" name="Next reviewer" flowable:assignee="${nextUser}"/>
  <serviceTask id="callback" name="Framework final callback" flowable:type="http">
    <extensionElements>
      <flowable:field name="requestMethod"><flowable:string>POST</flowable:string></flowable:field>
      <flowable:field name="requestUrl"><flowable:expression>${frameworkCallbackUrl}</flowable:expression></flowable:field>
      <flowable:field name="requestHeaders"><flowable:string>Content-Type: application/json</flowable:string></flowable:field>
      <flowable:field name="requestBody"><flowable:expression>{"processInstanceId":"${execution.processInstanceId}","businessId":"${businessId}","processDefinitionKey":"${processDefinitionKey}"}</flowable:expression></flowable:field>
    </extensionElements>
  </serviceTask>
  <endEvent id="end" name="Finished"/>
  <sequenceFlow id="f1" sourceRef="start" targetRef="review"/>
  <sequenceFlow id="f2" sourceRef="review" targetRef="decision"/>
  <sequenceFlow id="continue" name="Continue review" sourceRef="decision" targetRef="nextReview"><conditionExpression>${CONTINUE_REVIEW == true}</conditionExpression></sequenceFlow>
  <sequenceFlow id="finish" name="Finish directly" sourceRef="decision" targetRef="callback"><conditionExpression>${CONTINUE_REVIEW == false}</conditionExpression></sequenceFlow>
  <sequenceFlow id="f3" sourceRef="nextReview" targetRef="callback"/>
  <sequenceFlow id="f4" sourceRef="callback" targetRef="end"/>
</process></definitions>'''.replace('id="KEY"', 'id="' + KEY + '"')
    config = [{"taskDefinitionKey": "review", "roleKey": "starter", "assigneeMode": "single", "isStarterNode": True,
               "callbackUrl": None, "slots": [{"slotKey": "choose_next", "roleKey": "reviewer", "label": "Next reviewer",
               "variableName": "nextUser", "mode": "single", "required": True, "conditionalOn": "CONTINUE_REVIEW==true"}]},
              {"taskDefinitionKey": "nextReview", "roleKey": "reviewer", "assigneeMode": "single", "callbackUrl": None, "slots": []}]
    boundary = "ReproStatus6f42a819Boundary"
    pieces = [f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{KEY}.bpmn"\r\nContent-Type: application/xml\r\n\r\n{xml}\r\n',
              f'--{boundary}\r\nContent-Disposition: form-data; name="slotConfigJson"\r\n\r\n{json.dumps(config)}\r\n',
              f'--{boundary}--\r\n']
    return api("POST", "/api/flowable/bpmn/deploy", "".join(pieces).encode(), "multipart/form-data; boundary=" + boundary)


def observe(business):
    status = api("GET", f"/api/processes/{business}/status")
    listing = api("GET", f"/api/processes?businessId={business}")
    completed = api("GET", f"/api/processes?businessId={business}&status=completed")
    running = api("GET", f"/api/processes?businessId={business}&status=running")
    progress = api("GET", f"/api/processes/{business}/progress?processInstanceId={status['processInstanceId']}")
    rendering = api("GET", f"/api/processes/{business}/flow-render?processInstanceId={status['processInstanceId']}")
    return {"status": status["status"], "list": listing["items"][0]["status"], "completedFilterTotal": completed["total"],
            "runningFilterTotal": running["total"], "progress": progress["status"], "flowRender": rendering["status"],
            "instanceId": status["processInstanceId"], "callbackReceived": any(x["businessId"] == business for x in received)}


def scenario(label, continue_review):
    business = f"{KEY}-{label}-{'continue' if continue_review else 'finish'}"
    started = api("POST", "/api/processes/start", {
        "businessType": KEY, "businessId": business, "requestId": business, "businessTitle": "Isolated reproduction",
        "businessVariables": {"starterAssignee": USER}, "initialSlotSelections": [],
        "callback": {"url": "http://127.0.0.1:5014/completed", "timeoutSeconds": 5, "retryCount": 1},
    })
    instance = started["processInstanceId"]
    progress = api("GET", f"/api/processes/{business}/progress")
    task = progress["currentNodes"][0]["taskId"]
    preview = api("POST", f"/api/flowable/workbench/instances/{instance}/next-step", {
        "businessId": business, "taskId": task, "businessVariables": {},
    })
    results[f"{label}-{'continue' if continue_review else 'finish'}-preview"] = preview
    api("POST", "/api/tasks/complete", {"businessId": business, "taskId": task, "employeeId": USER, "action": 1,
        "comment": "Isolated status reproduction", "businessVariables": {"CONTINUE_REVIEW": continue_review},
        "nextSlotSelections": [{"slotKey": "choose_next", "users": [USER]}] if continue_review else []})
    if continue_review:
        progress = api("GET", f"/api/processes/{business}/progress")
        assert len(progress["currentNodes"]) == 1
        task = progress["currentNodes"][0]["taskId"]
        api("POST", "/api/tasks/complete", {"businessId": business, "taskId": task, "employeeId": USER,
            "action": 1, "comment": "Isolated final approval", "nextSlotSelections": []})
    for _ in range(50):
        if any(x["businessId"] == business for x in received):
            break
        time.sleep(.2)
    assert any(x["businessId"] == business for x in received), "Worker did not deliver the callback"
    time.sleep(1.2)  # Allow the normal ES refresh before testing its old list projection.
    result = observe(business)
    results[f"{label}-{'continue' if continue_review else 'finish'}"] = result
    print(f"{label} completed {business}: {json.dumps(result)}", flush=True)
    return business


sink = http.server.ThreadingHTTPServer(("127.0.0.1", 5014), Sink)
threading.Thread(target=sink.serve_forever, daemon=True).start()
try:
    server, log = start_server("baseline", OUT / "baseline/bin/Debug/net6.0/process.dll")
    try:
        results["deployment"] = deploy()
        old_business = scenario("baseline", False)
        baseline = results["baseline-finish"]
        assert baseline["callbackReceived"] and baseline["status"] == "completed" and baseline["list"] == "running", "Original bug did not reproduce"
    finally:
        server.terminate()
        server.wait(timeout=15)
        log.close()

    server, log = start_server("fixed", ROOT / "bin/Debug/net6.0/process.dll")
    try:
        results["fixed-existing-record"] = observe(old_business)
        scenario("fixed", False)
        scenario("fixed", True)
        for name in ("fixed-existing-record", "fixed-finish", "fixed-continue"):
            result = results[name]
            assert all(result[field] == "completed" for field in ("status", "list", "progress", "flowRender")), result
            assert result["completedFilterTotal"] == 1 and result["runningFilterTotal"] == 0, result
        choices = results["fixed-finish-preview"]["variables"][0]["options"]
        assert len(choices) == 2 and any(x["value"] is False and "结束流程" in x["targetNames"] for x in choices)
        print("PASS: original callback/list bug reproduced; fixed existing and new records, both branch paths and filters verified", flush=True)
    finally:
        server.terminate()
        server.wait(timeout=15)
        log.close()
finally:
    sink.shutdown()
    (OUT / "http-reproduction.json").write_text(json.dumps({"schema": SCHEMA, "results": results, "callbacks": received}, ensure_ascii=False, indent=2), encoding="utf-8")
