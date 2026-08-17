#!/usr/bin/env python3
"""Swap the planning agent's model: python3 tools/dev/flow-model.py gemini|glm

Edits langflow/planning-agent.v4.json in place. Deploy afterwards per
PLANNING-AGENT.md section 9 (DELETE flow id + upload), then run the eval gate.

What each direction sets, beyond the node itself:
  gemini -> agent stream=True  (Gemini's stream never hurt LangChain)
  glm    -> agent stream=False (GLM's reasoning_content chunks break
            LangChain's streaming parser; model_kwargs forces thinking on)
The system prompt is shared and model-agnostic: the dieted rules plus the
"TOOLS ARE THE ONLY WAY TO ACT" preamble help both models and hurt neither.
Keys: GEMINI_API_KEY / ZAI_API_KEY live in Langflow's variable store; refresh
them there (delete + recreate; values are write-only), not here.
"""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FLOW = ROOT / 'langflow/planning-agent.v4.json'
NODES = ROOT / 'langflow/model-nodes'
IDS = {'gemini': 'GeminiModel-v4', 'glm': 'ZaiModel-v4'}

target = sys.argv[1] if len(sys.argv) > 1 else ''
if target not in IDS:
    sys.exit(__doc__)

flow = json.loads(FLOW.read_text())
nodes, edges = flow['data']['nodes'], flow['data']['edges']
spec = json.loads((NODES / f'{target}.json').read_text())

nodes[:] = [n for n in nodes if n['data']['id'] not in IDS.values()] + [spec['node']]
edges[:] = [e for e in edges if e.get('source') not in IDS.values()] + [spec['edge']]

agent = next(n for n in nodes if n['data']['id'] == 'PlanningAgent-v4')
agent['data']['node']['template']['stream']['value'] = (target == 'gemini')

FLOW.write_text(json.dumps(flow, indent=2, ensure_ascii=False))

ids = {n['data']['id'] for n in nodes}
dangling = [e['id'] for e in edges if e['source'] not in ids or e['target'] not in ids]
tools = len([e for e in edges if e['target'] == 'PlanningAgent-v4' and 'toolsœ' in e['targetHandle']])
assert not dangling and tools == 11, f"structural check failed: dangling={dangling} tools={tools}"
print(f"flow now targets {target} ({IDS[target]}), stream={target == 'gemini'}, structure OK")
