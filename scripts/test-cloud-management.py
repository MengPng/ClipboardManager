"""Run on the host against the dedicated validation container; print no credentials."""
import concurrent.futures
import json
import secrets
import subprocess
import urllib.error
import urllib.request
import uuid

BASE = "http://127.0.0.1:53181"
CONTAINER = "clipboard-management-validation"

def request(method, path, body=None, token=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            data = r.read()
            return r.status, json.loads(data) if data else None
    except urllib.error.HTTPError as e:
        return e.code, None

def account(label):
    password = secrets.token_urlsafe(24)
    email = label + "-" + uuid.uuid4().hex + "@example.invalid"
    payload = json.dumps({"email": email, "displayName": label, "password": password})
    result = subprocess.run(["sudo", "docker", "exec", "-i", CONTAINER, "dotnet", "ClipboardManager.Cloud.Api.dll", "--provision-account"],
                            input=payload, text=True, capture_output=True)
    assert result.returncode == 0, "provision command failed (output suppressed)"
    status, session = request("POST", "/v1/auth/sign-in", {"email": email,"password": password,"deviceId": str(uuid.uuid4()),"deviceName": label})
    assert status == 200, f"sign-in failed: {status}"
    return session

owner, member, outsider = account("owner"), account("member"), account("outsider")
ot, mt, xt = (x["accessToken"] for x in (owner, member, outsider))
status, personal = request("GET", "/v1/workspaces", token=ot)
assert status == 200 and len(personal) == 1 and personal[0]["type"] == "personal", f"personal workspace: {status}"
status, team = request("POST", "/v1/workspaces", {"name":"Acceptance team"}, ot)
assert status == 200, f"create team: {status}"
wid = team["id"]
status, invite = request("POST", f"/v1/workspaces/{wid}/invitations", {"lifetimeSeconds":86400,"maxUses":1}, ot)
assert status == 200, f"invitation: {status}"
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    joins = list(pool.map(lambda token: request("POST", "/v1/invitations/join", {"code":invite["code"]}, token)[0], [mt,xt]))
assert sorted(joins) == [200,409], f"single-use concurrency: {joins}"
joined = member if joins[0] == 200 else outsider
jt = joined["accessToken"]
outside = xt if joins[0] == 200 else mt
status, members = request("GET", f"/v1/workspaces/{wid}/members", token=ot)
assert status == 200 and len(members) == 2, f"members: {status}"
assert request("GET", f"/v1/workspaces/{wid}/members", token=outside)[0] in (401,403)
assert request("POST", f"/v1/workspaces/{wid}/groups", {"name":"forbidden"}, jt)[0] in (401,403)
status, group = request("POST", f"/v1/workspaces/{wid}/groups", {"name":"Group A"}, ot)
assert status == 200, f"create group: {status}"
assert request("PUT", f"/v1/workspaces/{wid}/groups/{group['id']}/members", {"userIds":[joined["userId"]]}, ot)[0] == 204
folder = str(uuid.uuid4())
operation = {"id":str(uuid.uuid4()),"entityType":"collection_folder","entityId":folder,"operationType":0,"baseVersion":0,
             "payload":{"name":"synced","sortKey":1},"createdAt":"2026-09-10T00:00:00Z"}
body={"workspaceId":wid,"operations":[operation]}
status, pushed = request("POST", "/v1/sync/push", body, ot)
assert status == 200 and pushed["succeeded"], f"push: {status}"
assert request("POST", "/v1/sync/push", body, ot)[1]["serverVersions"] == pushed["serverVersions"]
assert request("POST", "/v1/sync/push", body, jt)[0] in (401,403)
status, pulled = request("GET", f"/v1/sync/pull?workspaceId={wid}&cursor=0", token=jt)
assert status == 200 and len(pulled["changes"]) == 1, f"pull: {status}"
assert request("GET", f"/v1/sync/pull?workspaceId={wid}&cursor=0", token=outside)[0] in (401,403)
status, refreshed=request("POST", "/v1/auth/refresh", {"refreshToken":owner["refreshToken"]})
assert status==200, f"refresh: {status}"
assert request("GET", "/v1/workspaces", token=ot)[0]==401
assert request("POST", "/v1/auth/refresh", {"refreshToken":owner["refreshToken"]})[0]==401
print(json.dumps({"provision":True,"personalWorkspace":True,"team":True,"invitationConcurrency":True,"groups":True,"memberWriteDenied":True,"crossTeamDenied":True,"pushReplay":True,"pull":True,"refreshRotation":True}))
