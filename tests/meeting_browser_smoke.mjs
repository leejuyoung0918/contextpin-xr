// Run against the local fixture EDGE and an isolated Chrome DevTools port.
// Node 20: node --experimental-websocket tests/meeting_browser_smoke.mjs
// Set OBSERVER_ASSEMBLY=1 against a fixture EDGE started with the assembly command.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";

const edge = process.env.OBSERVER_EDGE_URL || "http://127.0.0.1:8788";
const devtools = process.env.OBSERVER_DEVTOOLS_URL || "http://127.0.0.1:9288";
const assemblyMode = process.env.OBSERVER_ASSEMBLY === "1";
const output = resolve(process.env.OBSERVER_REPORT_DIR ||
  (assemblyMode ? "artifacts/assembly/browser-smoke" : "artifacts/meeting/browser-smoke"));
await mkdir(output, { recursive: true });
const downloads = new Set(await readdir(output));

class DevTools {
  constructor(url) {
    this.socket = new WebSocket(url);
    this.seq = 0;
    this.pending = new Map();
    this.exceptions = [];
    this.ready = new Promise((resolve, reject) => {
      this.socket.addEventListener("open", resolve, { once: true });
      this.socket.addEventListener("error", reject, { once: true });
    });
    this.socket.addEventListener("message", event => {
      const message = JSON.parse(event.data);
      if (message.method === "Runtime.exceptionThrown") {
        this.exceptions.push(message.params.exceptionDetails.exception?.description || message.params.exceptionDetails.text);
      }
      if (message.method === "Log.entryAdded" && message.params.entry.level === "error") {
        this.exceptions.push([message.params.entry.text, message.params.entry.url].filter(Boolean).join(" "));
      }
      const callback = this.pending.get(message.id);
      if (!callback) return;
      this.pending.delete(message.id);
      if (message.error) callback.reject(new Error(message.error.message));
      else callback.resolve(message.result);
    });
  }

  async send(method, params) {
    await this.ready;
    const id = ++this.seq;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(method + " timed out")), 12000);
      this.pending.set(id, {
        resolve: result => { clearTimeout(timer); resolve(result); },
        reject: error => { clearTimeout(timer); reject(error); },
      });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }

  async evaluate(expression) {
    const result = await this.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  }

  wait(expression, note) {
    return waitFor(() => this.evaluate(expression), note);
  }

  async click(selector) {
    await this.wait(`document.querySelector(${JSON.stringify(selector)})?.getClientRects().length &&
      !document.querySelector(${JSON.stringify(selector)}).disabled`, "clickable " + selector);
    await this.evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
  }

  async set(selector, value) {
    await this.evaluate(`document.querySelector(${JSON.stringify(selector)}).value = ${JSON.stringify(value)}`);
  }

  close() { this.socket.close(); }
}

async function waitFor(check, note) {
  const end = Date.now() + (assemblyMode ? 60000 : 15000);
  while (Date.now() < end) {
    const result = await check();
    if (result) return result;
    await new Promise(resolve => setTimeout(resolve, 120));
  }
  throw new Error("Timed out waiting for " + note);
}

async function tab() {
  const response = await fetch(devtools + "/json/new?about:blank", { method: "PUT" });
  assert.equal(response.ok, true, "isolated Chrome DevTools port");
  const result = await response.json();
  const page = new DevTools(result.webSocketDebuggerUrl);
  await page.send("Page.enable");
  await page.send("Runtime.enable");
  await page.send("Log.enable");
  await page.send("Page.navigate", { url: edge + "/meeting/" });
  await page.wait("document.querySelector('#create-submit')?.disabled === false", "profile loaded");
  return page;
}

const snapshotExpression = `(async () => {
  const {token, session_id} = JSON.parse(sessionStorage.getItem("contextpin-meeting-participant"));
  const response = await fetch("/v1/sessions/" + session_id, {headers: {Authorization: "Bearer " + token}});
  return response.json();
})()`;
const currentVersion = "document.querySelector('#model-canvas')?.dataset.version";
const checks = [];

const version = async page => page.wait(currentVersion, "drawn version");
const textContains = (selector, value) => `document.querySelector(${JSON.stringify(selector)})?.textContent.includes(${JSON.stringify(value)})`;
async function assemblySmoke(host) {
  await host.evaluate(`(async () => {
    const {ModelViewer} = await import('/meeting/viewer.js');
    const load = ModelViewer.prototype.load;
    ModelViewer.prototype.load = function (...args) {
      load.apply(this, args);
      window.lastModelFrame = { center: this.center, radius: this.radius, mount: args[2].mounting };
    };
  })()`);
  await host.set("#create-name", "조립체 검토");
  await host.click("#create-submit");
  const original = await version(host);
  await host.wait(textContains("#model-heading", "원본 모델"), "original SCAD displayed");
  assert.match(await host.evaluate("document.querySelector('#model-meta').textContent"), /100 × 34 × 48 mm.*OpenSCAD/);
  assert.match(await host.evaluate("document.querySelector('#assembly-summary').textContent"), /예제 조립체.*유지하는 결합 조건/s);
  const frame = await host.evaluate("window.lastModelFrame");
  assert(frame.radius > .05 && frame.radius < .07);
  checks.push("actual 100 mm OpenSCAD original shown in one fixed meter-scale viewer frame");

  await host.click("#param-section summary");
  await host.set("#param-outer_rounding", 5);
  await host.click("#edit-submit");
  const manual = await host.wait(`(${currentVersion}) !== ${JSON.stringify(original)} && (${currentVersion})`, "OpenSCAD manual edit");
  assert.deepEqual(await host.evaluate("window.lastModelFrame"), frame);
  let snapshot = await host.evaluate(snapshotExpression);
  assert.equal(snapshot.versions.find(item => item.version_id === manual).parameters.outer_rounding, 5);
  await host.click("#original-view");
  await host.wait(`${currentVersion} === ${JSON.stringify(original)}`, "compare against original in same frame");
  assert.deepEqual(await host.evaluate("window.lastModelFrame"), frame);
  checks.push("manual outer edit changes the GLB and original toggle preserves mount, framing and scale");

  await host.click("#sample-button");
  await host.wait("document.querySelectorAll('.candidate-list article').length === 3 && " +
    "document.querySelector('#round-status').textContent.includes('후보를 열어')", "three generated assembly alternatives");
  snapshot = await host.evaluate(snapshotExpression);
  const round = snapshot.rounds.at(-1);
  assert.equal(round.context.utterance.source_mode, "replay");
  const candidate = round.candidates[0];
  assert(round.candidates.every(item => item.status === "valid"));
  assert.equal(new Set(round.candidates.map(item =>
    snapshot.versions.find(model => model.version_id === item.version_id).geometry_hash)).size, 3);
  await host.click(".candidate-list article:first-child button");
  await host.wait(`${currentVersion} === ${JSON.stringify(candidate.version_id)}`, "first assembly alternative appears");
  assert.deepEqual(await host.evaluate("window.lastModelFrame"), frame);
  checks.push("three actual different OpenSCAD alternatives from clearly labelled REPLAY input");

  await host.set("#speech-text", "이 후보에서 모서리 반경을 8mm로 바꿔줘");
  await host.click("#text-submit");
  await waitFor(async () => {
    const current = await host.evaluate(snapshotExpression);
    return current.rounds.at(-1).round_id !== round.round_id && current.rounds.at(-1).status === "reviewable";
  }, "follow-up edit on current candidate");
  snapshot = await host.evaluate(snapshotExpression);
  const refine = snapshot.rounds.at(-1);
  assert.equal(refine.edit_base_version_id, candidate.version_id);
  assert.equal(refine.candidates.length, 1);
  const refined = refine.candidates[0].version_id;
  const versionInfo = snapshot.versions.find(item => item.version_id === refined);
  assert.equal(versionInfo.parameters.outer_rounding, 8);
  await host.click(".candidate-list article:first-child button");
  await host.wait(`${currentVersion} === ${JSON.stringify(refined)}`, "refined candidate appears");
  assert.deepEqual(await host.evaluate("window.lastModelFrame"), frame);
  checks.push("follow-up edit uses the visible candidate as its OpenSCAD base and never recenters it");

  await host.set("#decision-reason", "외형 모서리가 더 둥근 후보를 선택");
  await host.click(".decision-form .primary");
  await host.wait(textContains("#minutes-list", "외형 모서리가 더 둥근 후보를 선택"), "assembly decision recorded");
  const final = await host.evaluate(snapshotExpression);
  assert.equal(final.committed_version_id, refined);
  assert.equal(final.decisions.at(-1).assembly_record.selected_candidate_id, refine.candidates[0].candidate_id);
  await host.click("#download-zip");
  const zipFile = "contextpin-" + refined + ".zip";
  await waitFor(async () => (await readdir(output)).includes(zipFile) && !downloads.has(zipFile), "assembly ZIP downloaded");
  execFileSync("python3", ["-c", `import hashlib, json, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    record = json.loads(archive.read("assembly_meeting.json"))
    assert record["selected_candidate_id"] == sys.argv[2]
    assert record["selected_model_sha256"] == hashlib.sha256(archive.read("model.stl")).hexdigest()
    assert json.loads(archive.read("parameters.json"))["outer_rounding"] == 8
    assert hashlib.sha256(archive.read("object_2.scad")).hexdigest() == sys.argv[3]
`, resolve(output, zipFile), refine.candidates[0].candidate_id, versionInfo.assets["object_2.scad"].sha256]);
  checks.push("selected candidate, reason, original SCAD, parameters, and exact STL persisted in ZIP");

  const image = await host.send("Page.captureScreenshot", { format: "png" });
  await writeFile(resolve(output, "assembly-observer.png"), Buffer.from(image.data, "base64"));
  assert.deepEqual(host.exceptions, []);
  return { status: "pass", checks, zipFile };
}

let host, reviewer, browser;
try {
  const metadata = await (await fetch(devtools + "/json/version")).json();
  browser = new DevTools(metadata.webSocketDebuggerUrl);
  await browser.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: output });
  host = await tab();
  if (assemblyMode) {
    const report = await assemblySmoke(host);
    await writeFile(resolve(output, "report.json"), JSON.stringify(report, null, 2));
    console.log(JSON.stringify(report, null, 2));
  } else {
    await regularSmoke();
  }
} catch (error) {
  for (const [name, page] of [["host", host], ["reviewer", reviewer]]) {
    if (!page) continue;
    console.error(name, page.exceptions, await page.evaluate(`({ notice: document.querySelector('#notice')?.textContent,
      view: document.querySelector('#view-message')?.textContent, status: document.querySelector('#round-status')?.textContent })`));
    const image = await page.send("Page.captureScreenshot", { format: "png" });
    await writeFile(resolve(output, name + "-failure.png"), Buffer.from(image.data, "base64"));
  }
  throw error;
} finally {
  host?.close();
  reviewer?.close();
  browser?.close();
}

async function regularSmoke() {
  await host.set("#create-name", "진행자 검증");
  await host.click("#create-submit");
  const original = await version(host);
  assert.match(await host.evaluate("document.querySelector('#model-meta').textContent"), /100 × 36 × 60 mm/);
  checks.push("100 mm default model GLB rendered");

  const box = await host.evaluate(`(() => {
    const canvas = document.querySelector('#model-canvas');
    canvas.scrollIntoView({block: 'center'});
    const {x, y, width, height} = canvas.getBoundingClientRect();
    return {x: x + width / 2, y: y + height / 2};
  })()`);
  await host.send("Input.dispatchMouseEvent", { type: "mousePressed", button: "left", clickCount: 1, ...box });
  await host.send("Input.dispatchMouseEvent", { type: "mouseReleased", button: "left", clickCount: 1, ...box });
  await host.wait("document.querySelector('#parts [aria-pressed=true]')?.dataset.part", "semantic mesh hit selection");
  checks.push("WebGL mesh click selects a registered part");

  await host.click("#param-section summary");
  await host.set("#param-width_mm", 110);
  await host.evaluate(`(() => {
    const upstream = window.fetch.bind(window);
    let releaseFetch;
    const gate = new Promise(resolve => { releaseFetch = resolve; });
    window.releaseModelFetch = () => {
      window.fetch = upstream;
      releaseFetch();
    };
    window.fetch = async (url, options) => {
      if (String(url).endsWith("/preview.glb")) {
        window.modelFetchPending = true;
        await gate;
      }
      return upstream(url, options);
    };
  })()`);
  await host.click("#edit-submit");
  await host.wait("window.modelFetchPending", "new preview download starts");
  assert.equal(await host.evaluate(currentVersion), original);
  assert.equal(await host.evaluate("document.querySelector('#parts .part').disabled"), true);
  await host.evaluate("window.releaseModelFetch()");
  const edited = await host.wait(`(${currentVersion}) !== ${JSON.stringify(original)} && (${currentVersion})`, "edited model applied");
  assert.match(await host.evaluate("document.querySelector('#model-meta').textContent"), /110 × 36 × 60 mm/);
  await host.click("#download-stl");
  const stlFile = "contextpin-" + edited + ".stl";
  await waitFor(async () => (await readdir(output)).includes(stlFile) && !downloads.has(stlFile), "browser STL download");
  const stl = await readFile(resolve(output, stlFile));
  const triangleCount = stl.readUInt32LE(80);
  assert.equal(stl.length, 84 + 50 * triangleCount);
  let low = Infinity;
  let high = -Infinity;
  for (let offset = 84; offset < stl.length; offset += 50) {
    for (const vertexOffset of [12, 24, 36]) {
      const x = stl.readFloatLE(offset + vertexOffset);
      low = Math.min(low, x);
      high = Math.max(high, x);
    }
  }
  assert(Math.abs(high - low - 110) < .0001);
  checks.push("numeric edit keeps the last valid GLB until replacement and downloads exact 110 mm STL");

  await host.set("#decision-reason", "시제품 폭 테스트");
  await host.click(".decision-form .primary");
  await host.wait(textContains("#minutes-list", "시제품 폭 테스트"), "first model decision");
  await host.click("#invite-button");
  const code = await host.wait("document.querySelector('#invite-code').textContent.match(/^[0-9]{6}$/)?.[0]", "six-digit join code");
  await host.click("#invite-dialog form button");
  reviewer = await tab();
  await reviewer.set("#join-name", "검토자 검증");
  await reviewer.set("#join-code", code);
  await reviewer.click("#join-form button");
  assert.equal(await version(reviewer), edited);
  await host.wait(textContains("#participants", "검토자 검증"), "second participant synchronized");
  checks.push("second browser joined same committed model by code");

  await host.click("#sample-button");
  await host.wait("document.querySelectorAll('.candidate-list article').length === 3 && " +
    "document.querySelector('#round-status').textContent.includes('후보를 열어')", "three generated candidates");
  await reviewer.wait("document.querySelectorAll('.candidate-list article button').length === 3", "candidate event sync");
  let session = await host.evaluate(snapshotExpression);
  let round = session.rounds.at(-1);
  assert.equal(round.context.utterance.source_mode, "replay");
  const candidates = round.candidates;
  assert.equal(new Set(candidates.map(candidate => candidate.version_id)).size, 3);
  const hashes = candidates.map(candidate =>
    session.versions.find(version => version.version_id === candidate.version_id).geometry_hash);
  assert(hashes.every(Boolean));
  assert.equal(new Set(hashes).size, 3);
  for (const candidate of candidates) {
    const model = session.versions.find(item => item.version_id === candidate.version_id);
    assert.equal(model.build_mode, "generated");
    assert.equal(model.measurements.height_mm, 36);
  }
  checks.push("three distinguishable generated shapes with preserved height from labelled REPLAY evidence");

  await host.click(".candidate-list article:nth-child(1) button");
  await host.wait(`${currentVersion} === ${JSON.stringify(candidates[0].version_id)}`, "candidate A GLB");
  await host.set("#answer-interpretation", candidates[0].candidate_id);
  await host.click(".response-form:first-of-type button");
  await waitFor(async () => (await host.evaluate(snapshotExpression)).rounds.at(-1).responses.length === 1, "first response");
  await host.set("#answer-preference", candidates[1].candidate_id);
  await host.click(".response-form:nth-of-type(2) button");
  await waitFor(async () => (await host.evaluate(snapshotExpression)).rounds.at(-1).responses.length === 2, "second response");
  await reviewer.click(".candidate-list article:nth-child(2) button");
  await reviewer.wait(`${currentVersion} === ${JSON.stringify(candidates[1].version_id)}`, "candidate B GLB on second client");
  await reviewer.click("#round-content > button");
  await waitFor(async () => Object.keys((await host.evaluate(snapshotExpression)).rounds.at(-1).acks || {}).length === 1,
    "reviewer confirmation synchronized");
  await host.click(".candidate-list article:nth-child(2) button");
  const chosen = candidates[1].version_id;
  await host.wait(`${currentVersion} === ${JSON.stringify(chosen)}`, "candidate B on host");
  await host.set("#decision-reason", "B안의 측면 형상을 검토하기로 했다");
  await host.click(".decision-form .primary");
  await reviewer.wait(textContains("#minutes-list", "B안의 측면 형상을"), "decision synchronized to reviewer");
  session = await host.evaluate(snapshotExpression);
  round = session.rounds.at(-1);
  assert.equal(session.committed_version_id, chosen);
  assert.equal(session.decisions.at(-1).selected_model_version_id, chosen);
  assert.equal(session.decisions.at(-1).consensus_status, "confirmed");
  assert.deepEqual(round.responses.map(item => [item.kind, item.candidate_ids[0]]), [
    ["interpretation", candidates[0].candidate_id], ["preference", candidates[1].candidate_id],
  ]);
  checks.push("separate interpretation/preference, explicit review confirmation, and chosen version in minutes");

  await host.click("#download-zip");
  const zipFile = "contextpin-" + chosen + ".zip";
  await waitFor(async () => (await readdir(output)).includes(zipFile) && !downloads.has(zipFile), "export ZIP");
  execFileSync("python3", ["-c", `import hashlib, json, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    decision = json.loads(archive.read("decision.json"))
    assert decision["selected_model_version_id"] == sys.argv[2]
    assert hashlib.sha256(archive.read("preview.glb")).hexdigest() == sys.argv[3]
    assert hashlib.sha256(archive.read("model.stl")).hexdigest() == sys.argv[4]
`, resolve(output, zipFile), chosen, session.versions.find(item => item.version_id === chosen).assets["preview.glb"].sha256,
    session.versions.find(item => item.version_id === chosen).assets["print.stl"].sha256]);

  const olderDecisionButton = "#minutes-list .minute-card:nth-child(2) button";
  await host.click(olderDecisionButton);
  await host.wait(`${currentVersion} === ${JSON.stringify(edited)}`, "view older meeting version");
  assert.equal((await host.evaluate(snapshotExpression)).committed_version_id, chosen);
  await host.send("Page.reload");
  await host.wait(`${currentVersion} === ${JSON.stringify(chosen)}`, "resumed canonical version after reload");
  checks.push("immutable version ZIP, historical preview, and reload recovery");

  const image = await host.send("Page.captureScreenshot", { format: "png" });
  await writeFile(resolve(output, "meeting-observer.png"), Buffer.from(image.data, "base64"));
  assert.deepEqual([...host.exceptions, ...reviewer.exceptions], []);
  const report = { status: "pass", checks, stlFile, zipFile };
  await writeFile(resolve(output, "report.json"), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
}
