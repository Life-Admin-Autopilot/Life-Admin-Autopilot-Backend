"""Rebuild the Langflow flow from an export, adding the backend-integration components.

Hand-editing a Langflow export is unpleasant and easy to get subtly wrong, so the flow
is treated as a build artefact: export from Langflow, run this, import the result.
The component bodies live in ./components/*.py and are inlined into the nodes here, so
the Python stays reviewable in the repo instead of only inside a JSON string.

Node templates are not hand-written - they are built by the running Langflow itself via
POST /api/v1/custom_component, which is the same code path the UI uses when you paste a
component in. That means the generated nodes cannot drift from what this Langflow
version expects, and a component whose code does not build fails here rather than
silently importing as a red "blocked or outdated" node.

    # with Langflow running (default http://localhost:7860)
    python langflow/build_flow.py "C:/Users/Omar/Downloads/Life Admin Autopilot.json"
"""

from __future__ import annotations

import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).parent
COMPONENTS = ROOT / "components"
LANGFLOW_URL = "http://localhost:7860"

AGENT = "Agent-iSv6C"
PROMPT = "Prompt Template-6wmES"
CHAT_INPUT = "ChatInput-5sln6"
SAVE_TASK = "CustomComponent-v8wlq"
PYTHON_REPL = "PythonREPLComponent-RC763"

TRANSCRIBE = "CustomComponent-trn01"
STAGE_DOC = "CustomComponent-doc01"
READ_URL = "CustomComponent-url01"
CONFLICT = "CustomComponent-cfl01"

# Langflow serialises edge handles as JSON with every double quote replaced by this
# character, then embeds the result in the edge id. It is U+0153 (oe ligature) - copying
# the character out of a rendered export gives the wrong codepoint, so it is written as
# an escape and checked against the source export below.
QUOTE = "\u0153"


# ------------------------------------------------------------------ langflow client


class Langflow:
    """The running Langflow, used as the authority on component template shape."""

    def __init__(self, base_url: str = LANGFLOW_URL):
        self.base_url = base_url
        try:
            with urllib.request.urlopen(f"{base_url}/api/v1/auto_login", timeout=10) as reply:
                self.token = json.loads(reply.read())["access_token"]
        except urllib.error.URLError as error:
            msg = (
                f"Cannot reach Langflow at {base_url} ({error}).\n"
                "Start Langflow first - this script asks it to build the component "
                "templates so they match your exact version."
            )
            raise SystemExit(msg) from error

    def build_component(self, code: str) -> dict:
        request = urllib.request.Request(
            f"{self.base_url}/api/v1/custom_component",
            data=json.dumps({"code": code, "frontend_node": {}}).encode(),
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self.token}",
            },
        )
        try:
            with urllib.request.urlopen(request, timeout=60) as reply:
                payload = json.loads(reply.read())
        except urllib.error.HTTPError as error:
            detail = error.read().decode()[:2000]
            msg = f"Langflow refused to build the component:\n{detail}"
            raise SystemExit(msg) from error
        return payload.get("data", payload)


# ----------------------------------------------------------------------------- edges


def handle(payload: dict) -> str:
    """Render a handle dict the way Langflow stores it (sorted keys, U+0153 for quotes)."""
    return json.dumps(payload, sort_keys=True, separators=(",", ":")).replace('"', QUOTE)


def edge(source: str, source_handle: dict, target: str, target_handle: dict) -> dict:
    rendered_source = handle(source_handle)
    rendered_target = handle(target_handle)
    return {
        "animated": False,
        "className": "",
        "data": {"sourceHandle": source_handle, "targetHandle": target_handle},
        "id": f"xy-edge__{source}{rendered_source}-{target}{rendered_target}",
        "selected": False,
        "source": source,
        "sourceHandle": rendered_source,
        "target": target,
        "targetHandle": rendered_target,
    }


# ----------------------------------------------------------------------------- nodes


def as_node(node_id: str, template: dict, position: dict, size: dict) -> dict:
    return {
        "data": {
            "id": node_id,
            "node": template,
            "selected_output": template["outputs"][0]["name"],
            "showNode": True,
            "type": template["display_name"].replace(" ", ""),
        },
        "dragging": False,
        "id": node_id,
        "measured": size,
        "position": position,
        "selected": False,
        "type": "genericNode",
    }


def as_tool(template: dict) -> dict:
    """Turn a built component template into its tool-mode form.

    The UI does this when you flip the Tool Mode switch; the build endpoint does not,
    so it is reproduced here. The shape mirrors the Save Task node in the original
    export, which is a known-good tool node from this Langflow version.
    """
    tool_name = template["outputs"][0]["method"]
    description = template["description"]

    args = {}
    for name, field in template["template"].items():
        if isinstance(field, dict) and field.get("tool_mode"):
            args[name] = {
                "default": field.get("value", ""),
                "description": field.get("info", ""),
                "title": name.replace("_", " ").title(),
                "type": "string",
            }

    template["tool_mode"] = True
    template["outputs"] = [{
        "allows_loop": False,
        "cache": True,
        "display_name": "Toolset",
        "group_outputs": False,
        "hidden": None,
        "loop_types": None,
        "method": "to_toolkit",
        "name": "component_as_tool",
        "options": None,
        "required_inputs": None,
        "selected": "Tool",
        "tool_mode": True,
        "types": ["Tool"],
        "value": "__UNDEFINED__",
    }]
    template["template"]["tools_metadata"] = {
        "_input_type": "ToolsInput",
        "advanced": False,
        "display_name": "Actions",
        "dynamic": False,
        "info": "Modify tool names and descriptions to help agents understand when to use each tool.",
        "is_list": True,
        "list_add_label": "Add More",
        "name": "tools_metadata",
        "override_skip": False,
        "placeholder": "",
        "real_time_refresh": True,
        "required": False,
        "show": True,
        "title_case": False,
        "tool_mode": False,
        "trace_as_metadata": True,
        "track_in_telemetry": False,
        "type": "tools",
        "value": [{
            "args": args,
            "description": description,
            "display_description": description,
            "display_name": tool_name,
            "name": tool_name,
            "readonly": False,
            "status": True,
            "tags": [tool_name],
        }],
    }
    return template


# --------------------------------------------------------------------------- prompt

PROMPT_TEMPLATE = """[VOICE TRANSCRIPT]
{voice_transcript}

[ATTACHMENT]
{document_context}

[TYPED MESSAGE]
{user_message}

An empty section means the user did not provide it. Never invent content for one."""


SYSTEM_PROMPT = """Today's Date is: {current_date}

You are the Interactive Planning & Confirmation Agent for a life-admin assistant. You \
turn what the user said, typed, or photographed into draft tasks, show them, and guide \
the user through confirming or correcting each one.

INPUT SECTIONS
Every message carries three sections. Any of them may be empty, and empty means absent:
- [VOICE TRANSCRIPT] - what the user said, already transcribed. Treat it as their words.
- [ATTACHMENT] - an ATTACHED DOCUMENT block for a file the user uploaded. It lists a \
storedPath. Never alter that value; copy it exactly.
- [TYPED MESSAGE] - what the user typed. In an ongoing conversation this is their reply.
If a section is empty, say nothing about it. Never invent a task from empty input - ask \
what they would like to add instead.

MODES
1. EXTRACTION - a new command or brain dump arrives: extract the tasks and present them.
2. CONVERSATION - the user replies about a draft: update it in memory, show the change, \
and save when they confirm.

EXTRACTION OUTPUT
{
  "draftTasks": [
    {
      "title": "short imperative task title",
      "dueDate": "YYYY-MM-DDTHH:mm:ss.sssZ, or null if none can be inferred",
      "category": "Financial | Work/University | Health | Vehicle | Home | Personal | General",
      "priority": "normal | important | urgent",
      "status": "pending",
      "sourceType": "voice | text",
      "conflicts": []
    }
  ]
}

SPLITTING
One message often holds several unrelated actions. "Go swimming, pay the bills and \
text Ahmed" is three tasks, never one. Split on every distinct action, and give each \
its own title, date, category and priority. Do not merge them just because they arrived \
together, and do not invent extra tasks that were not asked for.

PRIORITY
Choose deliberately - do not label everything normal:
- urgent    : a stated deadline within 48 hours, an overdue obligation, anything with a \
penalty for lateness (fines, cut-off notices, expiring documents), or words like \
"urgent", "ASAP", "immediately", "ضروري", "حالا", "مستعجل".
- important : money, health, legal or study/work obligations with a real consequence - \
bills, appointments, renewals, exams, official paperwork.
- normal    : errands, chores, social and personal items with no penalty for slipping.
If the user states an urgency, that always wins over your own reading.

STATUS
Every new draft has status "pending". The only permitted values are pending, in \
progress, completed, overdue and cancelled. Never invent another one, and never set \
"overdue" yourself - that is worked out from the due date having passed.

RULES
1. Reply in the language the user used. If they spoke or wrote Arabic, answer in Arabic \
and keep the task title in Arabic too. Only the field names stay English.
2. Always show the drafts as a friendly markdown list (Title, Due Date, Category, \
Priority) before asking anything. The user has to see what you understood.
3. Then ask whether to save, or what to change.
4. When the user changes something, acknowledge it and re-show the updated draft.
5. Never invent a due date. If the user did not state or imply one, dueDate is null - do \
not guess "next week" or "end of month" to fill the gap.
6. A task with no due date can never produce a reminder, so it must not be saved. If the \
user confirms a task whose dueDate is null or missing, do not call save_task. Reply: "I \
cannot save this task because it's missing a due date. Could you please provide when it \
is due?"
7. When the user confirms a task that has a due date, call save_task ONCE with the final \
values. If the conversation contains an ATTACHED DOCUMENT block and the task relates to \
that file, pass its storedPath as document_path. Otherwise leave document_path empty.
8. Call save_task exactly once per task. When it comes back with saved: true, that task \
is finished - never call save_task for it again in this conversation, even if the user \
says "thanks" or repeats themselves. Re-saving creates a duplicate row.
9. Confirm one task at a time. After a save succeeds, name the next unconfirmed task and \
ask about that one.
10. Due dates are YYYY-MM-DDTHH:mm:ss.sssZ, and that Z means UTC. The user speaks in \
Cairo time, which is UTC+2, so SUBTRACT 2 HOURS from the clock time they said before \
writing it down. 9am becomes T07:00:00.000Z. 8pm becomes T18:00:00.000Z. Midnight-tonight \
becomes T22:00:00.000Z on the PREVIOUS date. A day with no time at all becomes \
T00:00:00.000Z and stays on its own date. Getting this wrong fires the reminder on the \
wrong day, so do the subtraction every time.
11. Resolve relative dates ("tomorrow", "next week") against Today's Date above.
12. sourceType is decided by which section the request arrived in, not by what it is \
about: "voice" only if [VOICE TRANSCRIPT] has content, otherwise "text". A typed message \
is always "text" even when it describes something you would normally say out loud. Never \
send "pdf" or "photo" - when a document is attached, save_task works that out from the \
file itself and overrides whatever you pass.
13. Before you show a draft, call find_conflicting_tasks for EACH one, with that draft's \
title and due date. Put what comes back in that draft's own conflicts list - conflicts \
belong to the single task they affect, never to the batch. Then:
- a time_clash: say what it clashes with and at what time, and ask whether to move it.
- a possible_duplicate: say it looks already saved and ask whether to skip it.
- checked: false means the check could not run. Say conflicts could not be checked. Never \
report a clear calendar you did not verify.
- if overdueCount is above zero, mention it once at the end, not per task.
Still call the tool when a draft has no due date; it will report duplicates.
14. save_task reports back honestly. If it returns saved: false, or says the document was \
not attached, tell the user plainly - never claim a save succeeded when it did not.
15. Use get_file_url when the user wants to see or open a file they uploaded earlier. \
Those links expire after 15 minutes, so fetch a fresh one rather than repeating an old \
link."""


def prompt_variable(name: str) -> dict:
    return {
        "advanced": False,
        "display_name": name,
        "dynamic": False,
        "field_type": "str",
        "fileTypes": [],
        "file_path": "",
        "info": "",
        "input_types": ["Message"],
        "list": False,
        "load_from_db": False,
        "multiline": True,
        "name": name,
        "placeholder": "",
        "required": False,
        "show": True,
        "title_case": False,
        "type": "str",
        "value": "",
    }


# ----------------------------------------------------------------------------- main


def build(source: Path, destination: Path) -> None:
    langflow = Langflow()
    flow = json.loads(source.read_text(encoding="utf-8"))
    nodes = flow["data"]["nodes"]
    edges = flow["data"]["edges"]

    by_id = {node["id"]: node for node in nodes}
    missing = [n for n in (AGENT, PROMPT, CHAT_INPUT, SAVE_TASK) if n not in by_id]
    if missing:
        msg = f"The export is missing expected nodes: {missing}"
        raise SystemExit(msg)

    # An edge whose id does not match what Langflow expects is silently dropped on
    # import, so prove the encoding against the export's own edges before adding any.
    for existing in edges:
        rebuilt = edge(
            existing["source"], existing["data"]["sourceHandle"],
            existing["target"], existing["data"]["targetHandle"],
        )
        if rebuilt["id"] != existing["id"]:
            msg = (
                "Edge id encoding does not match this Langflow version.\n"
                f"  export:  {existing['id']}\n"
                f"  rebuilt: {rebuilt['id']}"
            )
            raise SystemExit(msg)

    def template_for(code_file: str) -> dict:
        return langflow.build_component((COMPONENTS / code_file).read_text(encoding="utf-8"))

    # --- Transcribe Audio ---------------------------------------------------
    nodes.append(as_node(
        TRANSCRIBE, template_for("transcribe_audio.py"),
        {"x": -1180.0, "y": -430.0}, {"height": 460, "width": 320},
    ))

    # --- Stage Document -----------------------------------------------------
    nodes.append(as_node(
        STAGE_DOC, template_for("stage_document.py"),
        {"x": -1180.0, "y": 110.0}, {"height": 400, "width": 320},
    ))

    # --- Get File URL (agent tool) ------------------------------------------
    nodes.append(as_node(
        READ_URL, as_tool(template_for("read_url_tool.py")),
        {"x": 1147.0, "y": -560.0}, {"height": 260, "width": 320},
    ))

    # --- Find Conflicting Tasks (agent tool) --------------------------------
    nodes.append(as_node(
        CONFLICT, as_tool(template_for("conflict_check_tool.py")),
        {"x": 1147.0, "y": -880.0}, {"height": 300, "width": 320},
    ))

    # --- Replace the Save Task tool -----------------------------------------
    # The exported version posted to /api/Planning/commit, which no controller serves.
    # Rebuilt from source rather than patched, so its template matches its code exactly.
    save_template = as_tool(template_for("save_task_tool.py"))
    save_node = by_id[SAVE_TASK]
    save_node["data"]["node"] = save_template
    save_node["data"]["type"] = "SaveTask"
    save_node["data"]["selected_output"] = "component_as_tool"
    save_node["measured"] = {"height": 380, "width": 320}

    # --- Prompt: three labelled sections instead of one ---------------------
    prompt_node = by_id[PROMPT]["data"]["node"]
    prompt_node["template"]["template"]["value"] = PROMPT_TEMPLATE
    # current_date came from a hand-typed field that was already months stale; the Agent
    # substitutes {current_date} into the system prompt on every run instead.
    prompt_node["template"].pop("current_date", None)
    prompt_node["template"]["voice_transcript"] = prompt_variable("voice_transcript")
    prompt_node["template"]["document_context"] = prompt_variable("document_context")
    prompt_node["custom_fields"] = {
        "template": ["voice_transcript", "document_context", "user_message"]
    }

    # --- Agent: instructions covering voice, documents and the new tool -----
    by_id[AGENT]["data"]["node"]["template"]["system_prompt"]["value"] = SYSTEM_PROMPT

    # --- Drop the dead Python Interpreter node ------------------------------
    # It was never connected to anything and its body does not parse (a stray "ja" on
    # the line after @tool), so it could only ever fail if something did reach it.
    nodes[:] = [node for node in nodes if node["id"] != PYTHON_REPL]
    edges[:] = [e for e in edges if PYTHON_REPL not in (e["source"], e["target"])]

    # --- Wire the new nodes -------------------------------------------------
    for node_id, data_type, output_name, field_name in (
        (TRANSCRIBE, "TranscribeAudio", "transcript", "voice_transcript"),
        (STAGE_DOC, "StageDocument", "document_context", "document_context"),
    ):
        edges.append(edge(
            node_id,
            {"dataType": data_type, "id": node_id, "name": output_name,
             "output_types": ["Message"]},
            PROMPT,
            {"fieldName": field_name, "id": PROMPT, "inputTypes": ["Message"], "type": "str"},
        ))

    edges.append(edge(
        CONFLICT,
        {"dataType": "FindConflictingTasks", "id": CONFLICT, "name": "component_as_tool",
         "output_types": ["Tool"]},
        AGENT,
        {"fieldName": "tools", "id": AGENT, "inputTypes": ["Tool"], "type": "other"},
    ))

    edges.append(edge(
        READ_URL,
        {"dataType": "GetFileURL", "id": READ_URL, "name": "component_as_tool",
         "output_types": ["Tool"]},
        AGENT,
        {"fieldName": "tools", "id": AGENT, "inputTypes": ["Tool"], "type": "other"},
    ))

    # The Save Task edge already exists but carries the old dataType in its id.
    edges[:] = [e for e in edges if e["source"] != SAVE_TASK]
    edges.append(edge(
        SAVE_TASK,
        {"dataType": "SaveTask", "id": SAVE_TASK, "name": "component_as_tool",
         "output_types": ["Tool"]},
        AGENT,
        {"fieldName": "tools", "id": AGENT, "inputTypes": ["Tool"], "type": "other"},
    ))

    # Give the two new upstream inputs room on the left.
    by_id[CHAT_INPUT]["position"] = {"x": -1180.0, "y": 600.0}

    # A Langflow export embeds secret field values in clear text. This file is committed,
    # so every password-typed field is blanked on the way out - re-enter them in the UI
    # after importing. Without this, the Mistral key ships to GitHub.
    scrubbed = []
    for node in nodes:
        for field_name, field in node["data"]["node"]["template"].items():
            if not isinstance(field, dict) or not field.get("password"):
                continue
            # Langflow builds SecretStrInput with load_from_db=True, which means "this
            # value is the NAME of a global variable, go look it up". Typing an actual
            # password into such a field makes the component send an empty string - the
            # lookup silently misses. These fields hold literals, so turn it off.
            field["load_from_db"] = False
            if field.get("value"):
                field["value"] = ""
                scrubbed.append(f"{node['id']}.{field_name}")

    destination.write_text(json.dumps(flow, indent=2, ensure_ascii=False), encoding="utf-8")
    if scrubbed:
        print(f"blanked secrets: {', '.join(scrubbed)}")
    print(f"nodes: {len(nodes)}  edges: {len(edges)}")
    print(f"written: {destination}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    build(Path(sys.argv[1]), ROOT / "Life Admin Autopilot.json")
