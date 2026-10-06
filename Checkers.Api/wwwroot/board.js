// Test board for the Checkers REST Web API.
// The page knows no checkers rules: legal moves and resulting positions come from
// POST /v1/move/legal, player moves are confirmed with POST /v1/move/validate and
// engine moves come from POST /v1/move/suggest.
"use strict";

const INITIAL_FEN = "B:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,12";
const GAME_ID = "checkers-8x8";

const $ = (id) => document.getElementById(id);

const state = {
  fen: null,          // canonical PDN of the current position
  sideToMove: "B",
  moves: [],          // legal moves of the current position (from /v1/move/legal)
  human: "B",
  level: "medium",
  selected: null,     // square of the selected piece
  partial: [],        // landing squares clicked so far (multi-jumps)
  history: [],        // { fen, logEntry } before each move, for undo
  log: [],
  lastMove: null,     // { from, path }
  busy: false,
};

// ---------------------------------------------------------------- API

async function call(method, url, body) {
  const started = performance.now();
  const response = await fetch(url, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const ms = Math.round(performance.now() - started);
  let data = null;
  try { data = await response.json(); } catch { /* empty body */ }
  return { status: response.status, ok: response.ok, data, ms };
}

const post = (url, body) => call("POST", url, body);

function suggestRequest(fen, level, limits) {
  const body = { gameId: GAME_ID, state: { notation: "PDN", position: fen } };
  if (level) body.level = level;
  if (limits) body.limits = limits;
  return post("/v1/move/suggest", body);
}

function problemText(r) {
  return r.data?.detail || r.data?.title || `HTTP ${r.status}`;
}

// ---------------------------------------------------------------- geometry

// Same numbering as Checkers.Core.Board: square 1..32, black starts on 1..12 at the top.
function squareToCell(square) {
  const n = square - 1;
  const y = Math.floor(n / 4);
  const x = 2 * (3 - (n % 4)) + (y % 2);
  // White at the bottom by default; flip the board when the player has black.
  return state.human === "B" ? { row: 7 - y, col: x } : { row: y, col: 7 - x };
}

function parseFen(fen) {
  const pieces = new Map(); // square -> { side: "B"|"W", king }
  const [, ...sections] = fen.split(":");
  for (const section of sections) {
    const side = section[0];
    for (const token of section.slice(1).split(",").filter(Boolean)) {
      const king = token[0] === "K";
      pieces.set(Number(king ? token.slice(1) : token), { side, king });
    }
  }
  return pieces;
}

// ---------------------------------------------------------------- rendering

function render() {
  const board = $("board");
  board.replaceChildren();
  const pieces = parseFen(state.fen);
  const cells = Array.from({ length: 64 }, (_, i) => {
    const div = document.createElement("div");
    div.className = "sq " + ((Math.floor(i / 8) + (i % 8)) % 2 === 1 ? "dark" : "light");
    return div;
  });

  const humanToMove = state.sideToMove === state.human && !state.busy;
  const movable = new Set(humanToMove ? state.moves.map((m) => m.from) : []);
  const targets = new Set(humanToMove ? candidateMoves().map((m) => m.path[state.partial.length]).filter(Boolean) : []);

  for (let square = 1; square <= 32; square++) {
    const { row, col } = squareToCell(square);
    const cell = cells[row * 8 + col];
    cell.dataset.square = square;
    cell.addEventListener("click", () => onSquareClick(square));

    const num = document.createElement("span");
    num.className = "num";
    num.textContent = square;
    cell.append(num);

    if (state.lastMove && (state.lastMove.from === square || state.lastMove.path.includes(square))) cell.classList.add("last");
    if (state.partial.includes(square)) cell.classList.add("path");
    if (targets.has(square)) cell.classList.add("target");

    const piece = pieces.get(square);
    if (piece) {
      const div = document.createElement("div");
      div.className = "piece " + (piece.side === "B" ? "black" : "white");
      if (piece.king) div.textContent = "K";
      if (square === state.selected) div.classList.add("selected");
      else if (!state.selected && movable.has(square)) div.classList.add("movable");
      cell.append(div);
    }
  }
  board.append(...cells);

  $("fen").value = state.fen;
  $("undo").disabled = state.busy || state.history.length === 0;
  $("hint").disabled = !humanToMove || state.moves.length === 0;

  const log = $("log");
  log.replaceChildren(...state.log.map((entry) => {
    const li = document.createElement("li");
    li.textContent = entry.text;
    if (entry.engine) li.className = "engine";
    return li;
  }));
  log.scrollTop = log.scrollHeight;
}

function setStatus(text, kind = "") {
  const el = $("status");
  el.textContent = text;
  el.className = "status " + kind;
}

function sideName(side) {
  return side === "B" ? "Black" : "White";
}

function showEngineAnswer(r) {
  const info = $("engine-info");
  const rows = r.ok
    ? [
        ["bestMove", r.data.bestMove],
        ["pv", r.data.pv.join(" ")],
        ["scoreOrWDL", r.data.scoreOrWDL ?? "—"],
        ["depth", r.data.depth],
        ["nodes", r.data.nodes.toLocaleString("en-US")],
        ["tablebaseHit", r.data.info.tablebaseHit],
        ["timeMs (server)", r.data.info.timeMs],
        ["cached", r.data.info.cached],
        ["round trip", `${r.ms} ms`],
      ]
    : [["HTTP", r.status], ["error", problemText(r)], ["round trip", `${r.ms} ms`]];
  info.replaceChildren(...rows.flatMap(([k, v]) => {
    const dt = document.createElement("dt");
    dt.textContent = k;
    const dd = document.createElement("dd");
    dd.textContent = String(v);
    return [dt, dd];
  }));
  $("engine-raw").textContent = JSON.stringify(r.data, null, 2);
}

// ---------------------------------------------------------------- game flow

async function loadPosition(fen) {
  const r = await post("/v1/move/legal", { position: fen });
  if (!r.ok) return problemText(r);
  state.fen = r.data.position;
  state.sideToMove = r.data.sideToMove;
  state.moves = r.data.moves;
  state.selected = null;
  state.partial = [];
  return null;
}

/** Decides what happens next: game over, engine turn or player turn. */
async function nextTurn() {
  render();
  if (state.moves.length === 0) {
    setStatus(`Game over: ${sideName(state.sideToMove)} has no moves, ${sideName(state.sideToMove === "B" ? "W" : "B")} wins.`);
    return;
  }
  if (state.sideToMove !== state.human) {
    await engineMove();
    return;
  }
  const capture = state.moves[0].captured.length > 0 ? " (capture is mandatory)" : "";
  setStatus(`Your move — ${sideName(state.human)}${capture}`);
}

async function playMove(move, entry) {
  state.busy = true;
  await animateMove(move);
  state.busy = false;
  state.history.push({ fen: state.fen });
  state.log.push(entry);
  state.lastMove = { from: move.from, path: move.path };
  const error = await loadPosition(move.after);
  if (error) setStatus(error, "error");
}

const HOP_MS = 180;

/** Slides the piece along its path; captured pieces fade out as they are jumped. */
async function animateMove(move) {
  if (matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  const cellOf = (square) => $("board").querySelector(`[data-square="${square}"]`);
  const fromCell = cellOf(move.from);
  const piece = fromCell?.querySelector(".piece");
  if (!piece) return;

  const origin = fromCell.getBoundingClientRect();
  const frames = [{ transform: "translate(0, 0)" }, ...move.path.map((square) => {
    const r = cellOf(square).getBoundingClientRect();
    return { transform: `translate(${r.left - origin.left}px, ${r.top - origin.top}px)` };
  })];

  fromCell.style.overflow = "visible";
  piece.classList.remove("selected", "movable");
  piece.classList.add("moving");
  const duration = HOP_MS * move.path.length;
  const slide = piece.animate(frames, { duration, easing: "ease-in-out", fill: "forwards" });

  move.captured.forEach((square, i) => {
    cellOf(square)?.querySelector(".piece")?.animate(
      [{ opacity: 1, transform: "scale(1)" }, { opacity: 0, transform: "scale(0.6)" }],
      { duration: HOP_MS, delay: HOP_MS * i + HOP_MS / 2, fill: "forwards" });
  });

  await slide.finished;
}

async function engineMove() {
  state.busy = true;
  render();
  setStatus(`Engine (${state.level}) is thinking…`, "thinking");
  const r = await suggestRequest(state.fen, state.level);
  showEngineAnswer(r);
  state.busy = false;

  if (!r.ok) {
    setStatus(`Engine error: ${problemText(r)}`, "error");
    render();
    return;
  }
  const move = state.moves.find((m) => m.notation === r.data.bestMove);
  if (!move) {
    setStatus(`Engine returned ${r.data.bestMove}, which is not a legal move here.`, "error");
    render();
    return;
  }
  const side = sideName(state.sideToMove);
  await playMove(move, {
    engine: true,
    text: `${side}: ${move.notation}  [engine ${state.level}, depth ${r.data.depth}, ${r.data.info.timeMs} ms${r.data.info.tablebaseHit ? ", tablebase" : ""}]`,
  });
  await nextTurn();
}

async function humanMove(move) {
  state.busy = true;
  // Confirm with the spec endpoint as well.
  const v = await post("/v1/move/validate", { position: state.fen, move: move.notation });
  state.busy = false;
  if (!v.ok || !v.data.legal) {
    setStatus(`Server rejected ${move.notation}: ${v.ok ? "illegal" : problemText(v)}`, "error");
    state.selected = null;
    state.partial = [];
    render();
    return;
  }
  await playMove(move, { engine: false, text: `${sideName(state.sideToMove)}: ${move.notation}` });
  await nextTurn();
}

/** Moves of the selected piece that match the landing squares clicked so far. */
function candidateMoves() {
  if (state.selected == null) return [];
  return state.moves.filter((m) =>
    m.from === state.selected && state.partial.every((sq, i) => m.path[i] === sq));
}

function onSquareClick(square) {
  if (state.busy || state.sideToMove !== state.human || state.moves.length === 0) return;

  // (Re)select one of our movable pieces.
  if (state.partial.length === 0 && state.moves.some((m) => m.from === square)) {
    state.selected = state.selected === square ? null : square;
    render();
    return;
  }

  const next = candidateMoves().filter((m) => m.path[state.partial.length] === square);
  if (next.length === 0) {
    state.selected = null;
    state.partial = [];
    render();
    return;
  }

  state.partial.push(square);
  const complete = next.filter((m) => m.path.length === state.partial.length);
  if (complete.length === 1 && next.length === 1) {
    humanMove(complete[0]);
  } else {
    render(); // multi-jump: wait for the next landing square
  }
}

async function newGame(fen = INITIAL_FEN) {
  if (state.busy) return;
  const error = await loadPosition(fen);
  if (error) {
    $("fen-error").textContent = error;
    return;
  }
  $("fen-error").textContent = "";
  state.history = [];
  state.log = [];
  state.lastMove = null;
  await nextTurn();
}

async function undo() {
  if (state.busy) return;
  // Go back to the last position where it was the player's turn.
  while (state.history.length > 0) {
    const { fen } = state.history.pop();
    state.log.pop();
    if (fen[0] === state.human || state.history.length === 0) {
      state.lastMove = null;
      const error = await loadPosition(fen);
      if (error) setStatus(error, "error");
      break;
    }
  }
  render();
  if (state.sideToMove === state.human) setStatus(`Your move — ${sideName(state.human)}`);
  else await nextTurn();
}

// ---------------------------------------------------------------- acceptance checks

const MIDGAME = "B:W17,21,22,23,25,26,28,30,31:B1,3,5,6,9,10,11,12,15";
const TABLEBASE = "W:WK27,30:BK14";

const checks = [
  {
    name: "Health check returns ok",
    async run() {
      const r = await call("GET", "/healthz");
      return { pass: r.status === 200 && r.data?.ok === true, detail: `HTTP ${r.status}, workers: ${r.data?.workers}` };
    },
  },
  {
    name: "Tablebase position (≤ 8 pieces): < 50 ms, tablebaseHit",
    async run() {
      const r = await suggestRequest(TABLEBASE, "strong");
      if (!r.ok) return { pass: false, detail: problemText(r) };
      const i = r.data.info;
      return {
        pass: i.tablebaseHit && i.timeMs < 50,
        detail: `${TABLEBASE} → ${r.data.bestMove}, tablebaseHit ${i.tablebaseHit}, ${i.timeMs} ms${i.cached ? " (cached)" : ""}`,
      };
    },
  },
  {
    name: "Midgame, level strong: < 600 ms, legal move",
    async run() {
      const r = await suggestRequest(MIDGAME, "strong");
      if (!r.ok) return { pass: false, detail: problemText(r) };
      const v = await post("/v1/move/validate", { position: MIDGAME, move: r.data.bestMove });
      const legal = v.ok && v.data.legal;
      return {
        pass: legal && r.data.info.timeMs < 600,
        detail: `${r.data.bestMove} (legal: ${legal}), depth ${r.data.depth}, ${r.data.info.timeMs} ms${r.data.info.cached ? " (cached)" : ""}`,
      };
    },
  },
  {
    name: "Invalid PDN → 422",
    async run() {
      const r = await suggestRequest("B:W33:B1", "weak");
      return { pass: r.status === 422, detail: `HTTP ${r.status}: ${problemText(r)}` };
    },
  },
  {
    name: "Timeout → 504",
    async run() {
      // No level: the soft limit (5 s) is far beyond the hard limit (50 ms).
      const r = await suggestRequest(INITIAL_FEN, null, { softTimeMs: 5000, hardTimeMs: 50 });
      return { pass: r.status === 504, detail: `hardTimeMs 50 → HTTP ${r.status}, ${r.ms} ms` };
    },
  },
];

function renderChecks() {
  $("checks").replaceChildren(...checks.map((check, i) => {
    const tr = document.createElement("tr");
    const name = document.createElement("td");
    name.textContent = check.name;
    const detail = document.createElement("div");
    detail.className = "detail";
    detail.id = `check-detail-${i}`;
    name.append(detail);
    const result = document.createElement("td");
    result.className = "result";
    result.id = `check-result-${i}`;
    result.textContent = "—";
    const action = document.createElement("td");
    const button = document.createElement("button");
    button.className = "small";
    button.textContent = "Run";
    button.addEventListener("click", () => runCheck(i));
    action.append(button);
    tr.append(name, result, action);
    return tr;
  }));
}

async function runCheck(i) {
  const result = $(`check-result-${i}`);
  const detail = $(`check-detail-${i}`);
  result.textContent = "…";
  result.className = "result";
  try {
    const { pass, detail: text } = await checks[i].run();
    result.textContent = pass ? "PASS" : "FAIL";
    result.className = "result " + (pass ? "pass" : "fail");
    detail.textContent = text;
  } catch (e) {
    result.textContent = "FAIL";
    result.className = "result fail";
    detail.textContent = String(e);
  }
}

async function runAllChecks() {
  for (let i = 0; i < checks.length; i++) await runCheck(i);
}

// ---------------------------------------------------------------- health badge

async function refreshHealth() {
  const badge = $("health");
  try {
    const r = await call("GET", "/healthz");
    badge.textContent = `health: ${r.data?.ok ? "ok" : "down"} · workers ${r.data?.workers ?? 0}`;
    badge.className = "badge " + (r.data?.ok ? "ok" : "fail");
  } catch {
    badge.textContent = "health: unreachable";
    badge.className = "badge fail";
  }
}

// ---------------------------------------------------------------- wiring

$("new-game").addEventListener("click", () => newGame());
$("undo").addEventListener("click", undo);
$("hint").addEventListener("click", () => {
  if (state.busy || state.sideToMove !== state.human) return;
  engineMove(); // the engine plays this move for the player, then replies as the opponent
});
$("side").addEventListener("change", (e) => {
  state.human = e.target.value;
  state.selected = null;
  state.partial = [];
  if (!state.busy) nextTurn();
});
$("level").addEventListener("change", (e) => { state.level = e.target.value; });
$("set-fen").addEventListener("click", () => newGame($("fen").value.trim()));
$("fen").addEventListener("keydown", (e) => { if (e.key === "Enter") newGame($("fen").value.trim()); });
$("copy-fen").addEventListener("click", () => navigator.clipboard?.writeText(state.fen));
$("run-checks").addEventListener("click", runAllChecks);

renderChecks();
refreshHealth();
setInterval(refreshHealth, 5000);
newGame();
