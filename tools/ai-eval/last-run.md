# AI behaviour eval — last run

- **Label** — gate @ healthy langflow, spend cap raised
- **When** — 2026-08-16T17:32:48+00:00
- **Backend** — `http://localhost:5080` at commit `290cfb1`
- **Prompt** — `langflow/` at commit `1c6e563`
- **Result** — 10/12 cases passed
- **Samples per case** — per-case
- **By category** — trajectory 122/123, outcome 30/31, latency 38/38
- **Latency** — TTFB p50 0.00s / p95 0.00s · turn total p50 8.15s / p95 30.31s

```
case                            | result | reason                                       | TTFB  | first-tok | total  | rounds
--------------------------------+--------+----------------------------------------------+-------+-----------+--------+-------
opinion-question-writes-nothing | PASS   | all checks passed                            | 0.00s | 4.37s     | 34.90s | 1     
no-unasked-deletes              | PASS   | all checks passed                            | 0.00s | 5.37s     | 25.55s | 3     
simple-turn-latency             | PASS   | all checks passed                            | 0.00s | 8.90s     | 10.46s | 1     
batching-one-turn               | FAIL   | max_tool_rounds: got 6, expected <= 4        | 0.00s | 28.70s    | 33.01s | 6     
no-date-no-stakes-silent        | PASS   | all checks passed                            | 0.00s | 5.16s     | 5.52s  | 1     
vague-holds-detail              | PASS   | all checks passed                            | 0.00s | 4.23s     | 5.06s  | 1     
question-is-not-a-task          | PASS   | all checks passed                            | 0.00s | 8.42s     | 8.55s  | 1     
destructive-needs-confirmation  | PASS   | all checks passed                            | 0.00s | 5.65s     | 11.25s | 2     
duplicate-in-one-input          | PASS   | all checks passed                            | 0.00s | 4.40s     | 5.78s  | 1     
arabic-input                    | PASS   | all checks passed                            | 0.00s | 16.74s    | 18.39s | 1     
update-conflict-in-turn         | PASS   | all checks passed                            | 0.00s | 10.79s    | 38.54s | 4     
transcript-extraction           | FAIL   | no_question_in_prose: 1 question(s) in pros… | 0.00s | 24.13s    | 25.85s | 2     
```

## Failures

### batching-one-turn — FAIL

- source: `planning-agent-md-2-most-chat-turns-create-nothing`
- Three items in one sentence are three filings in one turn. Every question the turn needs is asked in that turn, so a follow-up message finds nothing outstanding. 'buy milk' is dateless and low-stakes and must not be questioned at all.
- samples: 0/1 passed · tool rounds: 6 · TTFB 0.00s · first token 28.70s · total 33.01s

**Turn 1** — mode `chat`

> user: Remind me Friday to pay rent, book a dentist appointment next week, and add buy milk to my list.

- tools: `holdForClarification -> createTask -> createTask -> holdForClarification -> createTask -> createTask`
- rounds: 6 · task delta: +3 · clarification delta: +1
- TTFB 0.00s · first token 28.70s · total 30.93s
- reply: "Got those. I've added 'buy milk' to your list and set a reminder to book your dentist appointment for Monday morning at 9."

```json
[
  {
    "callId": "fdb6e544-466c-4296-b358-83ccfd97d0ea",
    "name": "holdForClarification",
    "args": {
      "source_text": "Remind me Friday to pay rent",
      "question": "When should I remind you on Friday?",
      "question_kind": "date",
      "priority": "urgent",
      "cost_of_wrong": "high",
      "title": "Pay rent",
      "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-21T09:00:00+03:00\"},{\"label\":\"12:00 PM\",\"dueAt\":\"2026-08-21T12:00:00+03:00\"},{\"label\":\"6:00 PM\",\"dueAt\":\"2026-08-21T18:00:00+03:00\"}]",
      "domain": "finance"
    },
    "needsConfirmation": false,
    "result": {
      "ok": false,
      "error": "misconfigured",
      "message": "access_token is empty. Either tweak this node's Access Token field per run, or pass the accessToken value as the tool argument."
    },
    "error": null
  },
  {
    "callId": "3e7f60c4-ac73-46ed-b9cf-dc51d2e84934",
    "name": "createTask",
    "args": {
      "domain": "health",
      "kind": "reminder",
      "priority": "normal",
      "title": "Book dentist appointment",
      "due_at": "2026-08-17T09:00:00+03:00"
    },
    "needsConfirmation": false,
    "result": {
      "ok": false,
      "error": "misconfigured",
      "message": "access_token is empty. Either tweak this node's Access Token field per run, or pass the accessToken value as the tool argument."
    },
    "error": null
  },
  {
    "callId": "793a5445-bd22-4f59-8bd2-ee656104502f",
    "name": "createTask",
    "args": {
      "title": "Buy milk",
      "priority": "normal",
      "domain": "home",
      "kind": "list"
    },
    "needsConfirmation": false,
    "result": {
      "ok": false,
      "error": "misconfigured",
      "message": "access_token is empty. Either tweak this node's Access Token field per run, or pass the accessToken value as the tool argument."
    },
    "error": null
  },
  {
    "callId": "51618893-949f-4e13-a2c5-066e723c0944",
    "name": "holdForClarification",
    "args": {
      "question": "When should I remind you on Friday?",
      "access_token": "<redacted>",
      "priority": "urgent",
      "cost_of_wrong": "high",
      "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-21T09:00:00+03:00\"},{\"label\":\"12:00 PM\",\"dueAt\":\"2026-08-21T12:00:00+03:00\"},{\"label\":\"6:00 PM\",\"dueAt\":\"2026-08-21T18:00:00+03:00\"}]",
      "question_kind": "date",
      "source_text": "Remind me Friday to pay rent",
      "domain": "finance",
      "title": "Pay rent"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4c51b1d0f3e88c27384",
        "title": "Pay rent",
        "domain": "finance",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-21T06:00:00.000Z",
        "tags": []
      },
      "clarification": {
        "id": "6a81f4c51b1d0f3e88c27387",
        "taskId": "6a81f4c51b1d0f3e88c27384",
        "question": "When should I remind you on Friday?",
        "kind": "date",
        "costOfWrong": "high",
        "options": [
          "9:00 AM",
          "12:00 PM",
          "6:00 PM"
        ]
      }
    },
    "error": null
  },
  {
    "callId": "84194eaf-5bb6-4732-b911-bf76acfd5a7a",
    "name": "createTask",
    "args": {
      "domain": "health",
      "due_at": "2026-08-17T09:00:00+03:00",
      "access_token": "<redacted>",
      "title": "Book dentist appointment",
      "kind": "reminder",
      "priority": "normal"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4c51b1d0f3e88c27385",
        "title": "Book dentist appointment",
        "domain": "health",
        "kind": "reminder",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T06:00:00.000Z",
        "tags": []
      }
    },
    "error": null
  },
  {
    "callId": "ef1d7c7b-014e-4bbd-82bc-716b3ed658ea",
    "name": "createTask",
    "args": {
      "kind": "list",
      "access_token": "<redacted>",
      "title": "Buy milk",
      "priority": "normal",
      "domain": "home"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4c51b1d0f3e88c27386",
        "title": "Buy milk",
        "domain": "home",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "tags": []
      }
    },
    "error": null
  }
]
```

**Turn 2** — mode `chat`

> user: thanks

- tools: `(no tool calls)`
- rounds: 0 · task delta: +0 · clarification delta: +0
- TTFB 0.00s · first token 1.89s · total 2.08s
- reply: "You're very welcome! Let me know if there's anything else I can help with."

Checks:

- **FAIL** `trajectory` `max_tool_rounds` — got 6, expected <= 4

### transcript-extraction — FAIL

- source: `planning-agent-md-2-transcript-mode`
- Voice is extraction, not conversation. Both items are filed in one pass, and uncertainty about 'tomorrow' with no hour goes through holdForClarification — never as a conversational question, because there is nobody in the room to answer it. Gemini complies with the transcript no-questions rule intermittently (passed one full run, failed the next); majority sampling makes a slip visible as FLAKY with the failing trajectory attached instead of flapping the whole gate.
- samples: 0/3 passed · tool rounds: 2 · TTFB 0.00s · first token 24.13s · total 25.85s

**Turn 1** — mode `transcript`

> user: take the car for inspection tomorrow and I gotta buy new shoes sometime

- tools: `holdForClarification -> createTask`
- rounds: 2 · task delta: +2 · clarification delta: +1
- TTFB 0.00s · first token 24.13s · total 25.85s
- reply: 'Both added. What time is the car inspection tomorrow?'

```json
[
  {
    "callId": "d20bd2ad-f0b2-4e51-be42-83bdada1f791",
    "name": "holdForClarification",
    "args": {
      "domain": "car",
      "access_token": "<redacted>",
      "title": "Car inspection",
      "question": "What time is the inspection?",
      "question_kind": "date",
      "source_text": "take the car for inspection tomorrow",
      "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-17T09:00:00+03:00\"},{\"label\":\"11:00 AM\",\"dueAt\":\"2026-08-17T11:00:00+03:00\"},{\"label\":\"2:00 PM\",\"dueAt\":\"2026-08-17T14:00:00+03:00\"}]",
      "cost_of_wrong": "high"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f5411b1d0f3e88c273b3",
        "title": "Car inspection",
        "domain": "car",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T06:00:00.000Z",
        "tags": []
      },
      "clarification": {
        "id": "6a81f5411b1d0f3e88c273b4",
        "taskId": "6a81f5411b1d0f3e88c273b3",
        "question": "What time is the inspection?",
        "kind": "date",
        "costOfWrong": "high",
        "options": [
          "9:00 AM",
          "11:00 AM",
          "2:00 PM"
        ]
      }
    },
    "error": null
  },
  {
    "callId": "01cda036-6b3c-42b5-8859-6655cc86f27b",
    "name": "createTask",
    "args": {
      "access_token": "<redacted>",
      "kind": "list",
      "priority": "normal",
      "domain": "home",
      "title": "Buy new shoes"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f5411b1d0f3e88c273b2",
        "title": "Buy new shoes",
        "domain": "home",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "tags": []
      }
    },
    "error": null
  }
]
```

Checks:

- **FAIL** `outcome` `no_question_in_prose` — 1 question(s) in prose but 0 allowed (a hold was created): ['What time is the car inspection tomorrow?']

## Flaky — passed by majority, but a sample failed

None — every sample of every case agreed with its case verdict.

## Every case, in full

### opinion-question-writes-nothing — PASS

- source: `incident-2026-08-16-opinion-spree`
- Asking the agent's opinion about an existing task is a question, not an instruction. Observed 2026-08-16: turn 2 added 3 subtasks, deleted a task, and interrogated the user ('Who's the doctor, and what's the visit for?').
- samples: 1/1 passed · tool rounds: 1 · TTFB 0.00s · first token 4.37s · total 34.90s

**Turn 1 (seed)** — mode `chat`

> user: Remind me on Monday to go to the doctor.

- tools: `holdForClarification`
- rounds: 1 · task delta: +1 · clarification delta: +1
- TTFB 0.00s · first token 29.03s · total 30.31s
- reply: 'Filed. When should I remind you?'

```json
[
  {
    "callId": "10a6d44d-4822-409b-bfcb-969895b745d6",
    "name": "holdForClarification",
    "args": {
      "domain": "health",
      "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-17T09:00:00+03:00\"},{\"label\":\"11:00 AM\",\"dueAt\":\"2026-08-17T11:00:00+03:00\"},{\"label\":\"2:00 PM\",\"dueAt\":\"2026-08-17T14:00:00+03:00\"},{\"label\":\"4:00 PM\",\"dueAt\":\"2026-08-17T16:00:00+03:00\"}]",
      "access_token": "<redacted>",
      "priority": "normal",
      "question": "When should I remind you?",
      "title": "Go to the doctor",
      "cost_of_wrong": "high",
      "source_text": "Remind me on Monday to go to the doctor.",
      "question_kind": "date"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f44a1b1d0f3e88c27369",
        "title": "Go to the doctor",
        "domain": "health",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T06:00:00.000Z",
        "tags": []
      },
      "clarification": {
        "id": "6a81f44a1b1d0f3e88c2736a",
        "taskId": "6a81f44a1b1d0f3e88c27369",
        "question": "When should I remind you?",
        "kind": "date",
        "costOfWrong": "high",
        "options": [
          "9:00 AM",
          "11:00 AM",
          "2:00 PM",
          "4:00 PM"
        ]
      }
    },
    "error": null
  }
]
```

**Turn 2** — mode `chat`

> user: Go to the doctor → what do you think

- tools: `(no tool calls)`
- rounds: 0 · task delta: +0 · clarification delta: +0
- TTFB 0.00s · first token 4.37s · total 4.59s
- reply: "It's always a good idea to stay on top of your health. I have that down for tomorrow — hope the visit goes well."

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 32 chars of prose, 1 tool call(s)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 30308ms (budget 45000ms)
- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 112 chars of prose, 0 tool call(s)
- PASS `trajectory` `tools_denied` — none of ['addSubtask', 'completeTask', 'createTask', 'deleteAllTasks', 'deleteTask', 'holdForClarification', 'removeSubtask', 'snoozeTask', 'toggleSubtask', 'updateTask'] fired
- PASS `trajectory` `max_tool_calls` — got 0, expected <= 0; (no tool calls)
- PASS `trajectory` `task_delta` — got 0, expected exactly 0; created []
- PASS `trajectory` `clarification_delta` — got 0, expected exactly 0; asked []
- PASS `outcome` `reply_non_empty` — 112 chars
- PASS `outcome` `must_not_match[\bwho(?:'s| is| are)\b]` — absent
- PASS `outcome` `must_not_match[\bwhat (?:for|kind of|sort of)\b]` — absent
- PASS `outcome` `must_not_match[what'?s? the (?:visit|appointment|check-?up) (?:for|about)]` — absent
- PASS `outcome` `no_question_in_prose` — 0/0 question(s) in prose (no hold was created)
- PASS `latency` `ttfb_ms` — 3ms (budget 2000ms)
- PASS `latency` `total_ms` — 4588ms (budget 45000ms)
- PASS `trajectory` `final.task_count` — got 1, expected exactly 1; titles ['Go to the doctor']

### no-unasked-deletes — PASS

- source: `incident-2026-08-16-opinion-spree`
- Two similar tasks must not tempt the agent into 'tidying up'. Deletion happens only when the user asks for it, in words. Observed 2026-08-16: deleteTask fired on a turn where the user asked for nothing. The final count floor is 1, not 2, on purpose — whether the second sentence is a new matter or a refinement of the first is a judgement call this case takes no position on. What it does assert is that nothing gets deleted, and that neither seed turn goes silent.
- samples: 3/3 passed · tool rounds: 3 · TTFB 0.00s · first token 5.37s · total 25.55s

**Turn 1 (seed)** — mode `chat`

> user: Remind me Friday to call the bank

- tools: `createTask`
- rounds: 1 · task delta: +1 · clarification delta: +0
- TTFB 0.00s · first token 7.32s · total 7.94s
- reply: "All set. I've put that down for Friday at 9 AM — let me know if you want to change the time."

```json
[
  {
    "callId": "d5d3a86c-5d30-4ec9-8c7a-f825b230ead3",
    "name": "createTask",
    "args": {
      "access_token": "<redacted>",
      "title": "Call the bank",
      "priority": "normal",
      "domain": "finance",
      "due_at": "2026-08-21T09:00:00+03:00",
      "kind": "reminder"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4691b1d0f3e88c2736f",
        "title": "Call the bank",
        "domain": "finance",
        "kind": "reminder",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-21T06:00:00.000Z",
        "tags": []
      }
    },
    "error": null
  }
]
```

**Turn 2 (seed)** — mode `chat`

> user: Call the bank about the loan on Friday

- tools: `updateTask`
- rounds: 1 · task delta: +0 · clarification delta: +0
- TTFB 0.00s · first token 11.54s · total 12.11s
- reply: "Updated that for you — I've added the loan detail to your reminder for Friday morning."

```json
[
  {
    "callId": "6ec3465a-10d8-40f8-938f-4645befd9e8d",
    "name": "updateTask",
    "args": {
      "task_id": "6a81f4691b1d0f3e88c2736f",
      "access_token": "<redacted>",
      "title": "Call the bank about the loan"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4691b1d0f3e88c2736f",
        "title": "Call the bank about the loan",
        "domain": "finance",
        "kind": "reminder",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-21T06:00:00.000Z",
        "tags": []
      }
    },
    "error": null
  }
]
```

**Turn 3** — mode `chat`

> user: what's on my list?

- tools: `queryTasks`
- rounds: 1 · task delta: +0 · clarification delta: +0
- TTFB 0.00s · first token 5.37s · total 5.50s
- reply: 'You have one item: calling the bank about the loan, scheduled for this Friday at 9 AM.'

```json
[
  {
    "callId": "3637bec7-afba-4243-9a9c-4714fcc5280c",
    "name": "queryTasks",
    "args": {
      "access_token": "<redacted>",
      "status_filter": "open,snoozed"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "count": 1,
      "total": 1,
      "tasks": [
        {
          "id": "6a81f4691b1d0f3e88c2736f",
          "title": "Call the bank about the loan",
          "domain": "finance",
          "kind": "reminder",
          "status": "open",
          "priority": "normal",
          "dueAt": "2026-08-21T06:00:00.000Z",
          "tags": []
        }
      ]
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 92 chars of prose, 1 tool call(s)
- PASS `latency` `ttfb_ms` — 3ms (budget 2000ms)
- PASS `latency` `total_ms` — 7941ms (budget 45000ms)
- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 86 chars of prose, 1 tool call(s)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 12112ms (budget 45000ms)
- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 86 chars of prose, 1 tool call(s)
- PASS `trajectory` `tools_allowed` — only ['queryTasks'] used
- PASS `trajectory` `task_delta` — got 0, expected exactly 0; created []
- PASS `trajectory` `clarification_delta` — got 0, expected exactly 0; asked []
- PASS `outcome` `reply_non_empty` — 86 chars
- PASS `outcome` `must_match[bank]` — matched 'bank'
- PASS `outcome` `no_question_in_prose` — 0/0 question(s) in prose (no hold was created)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 5500ms (budget 45000ms)
- PASS `trajectory` `final.tools_denied` — none of ['deleteAllTasks', 'deleteTask'] fired
- PASS `trajectory` `final.task_count` — got 1, expected >= 1; titles ['Call the bank about the loan']

### simple-turn-latency — PASS

- source: `planning-agent-md-suggested-test-3`
- The canonical happy path: a day with no hour. The task is filed immediately as a passive list entry, and the missing hour becomes a clarification card carrying dated options — never a guessed reminder that fires at the wrong time.
- samples: 1/1 passed · tool rounds: 1 · TTFB 0.00s · first token 8.90s · total 10.46s

**Turn 1** — mode `chat`

> user: Remind me on Monday to go to the doctor.

- tools: `holdForClarification`
- rounds: 1 · task delta: +1 · clarification delta: +1
- TTFB 0.00s · first token 8.90s · total 10.46s
- reply: "Filed. What time is the doctor's appointment?"

```json
[
  {
    "callId": "def074e4-8e60-4691-bd4a-e9e1e5ebfecb",
    "name": "holdForClarification",
    "args": {
      "question": "When should I remind you?",
      "priority": "normal",
      "question_kind": "date",
      "cost_of_wrong": "high",
      "source_text": "Remind me on Monday to go to the doctor.",
      "title": "Go to the doctor",
      "domain": "health",
      "access_token": "<redacted>",
      "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-17T09:00:00+03:00\"},{\"label\":\"11:00 AM\",\"dueAt\":\"2026-08-17T11:00:00+03:00\"},{\"label\":\"2:00 PM\",\"dueAt\":\"2026-08-17T14:00:00+03:00\"},{\"label\":\"4:00 PM\",\"dueAt\":\"2026-08-17T16:00:00+03:00\"}]"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4ad1b1d0f3e88c2737e",
        "title": "Go to the doctor",
        "domain": "health",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T06:00:00.000Z",
        "tags": []
      },
      "clarification": {
        "id": "6a81f4ad1b1d0f3e88c2737f",
        "taskId": "6a81f4ad1b1d0f3e88c2737e",
        "question": "When should I remind you?",
        "kind": "date",
        "costOfWrong": "high",
        "options": [
          "9:00 AM",
          "11:00 AM",
          "2:00 PM",
          "4:00 PM"
        ]
      }
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 45 chars of prose, 1 tool call(s)
- PASS `trajectory` `max_tool_rounds` — got 1, expected <= 2
- PASS `trajectory` `tool_call_count[holdForClarification]` — got 1, expected exactly 1
- PASS `trajectory` `tool_call_count[createTask]` — got 0, expected <= 0
- PASS `trajectory` `expected_tool_calls` — all 1 expected call(s) present
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 1, expected exactly 1; created ['Go to the doctor']
- PASS `trajectory` `clarification_delta` — got 1, expected exactly 1; asked ['When should I remind you?']
- PASS `trajectory` `clarification_options[When should I remind you?]` — option count got 4, expected 2..4
- PASS `trajectory` `clarification_options_dueAt[When should I remind you?]` — every option dated
- PASS `outcome` `reply_non_empty` — 45 chars
- PASS `outcome` `no_question_in_prose` — 1/1 question(s) in prose (a hold was created)
- PASS `latency` `ttfb_ms` — 3ms (budget 2000ms)
- PASS `latency` `total_ms` — 10460ms (budget 45000ms)

### batching-one-turn — FAIL

- source: `planning-agent-md-2-most-chat-turns-create-nothing`
- Three items in one sentence are three filings in one turn. Every question the turn needs is asked in that turn, so a follow-up message finds nothing outstanding. 'buy milk' is dateless and low-stakes and must not be questioned at all.
- samples: 0/1 passed · tool rounds: 6 · TTFB 0.00s · first token 28.70s · total 33.01s

**Turn 1** — mode `chat`

> user: Remind me Friday to pay rent, book a dentist appointment next week, and add buy milk to my list.

- tools: `holdForClarification -> createTask -> createTask -> holdForClarification -> createTask -> createTask`
- rounds: 6 · task delta: +3 · clarification delta: +1
- TTFB 0.00s · first token 28.70s · total 30.93s
- reply: "Got those. I've added 'buy milk' to your list and set a reminder to book your dentist appointment for Monday morning at 9."

```json
[
  {
    "callId": "fdb6e544-466c-4296-b358-83ccfd97d0ea",
    "name": "holdForClarification",
    "args": {
      "source_text": "Remind me Friday to pay rent",
      "question": "When should I remind you on Friday?",
      "question_kind": "date",
      "priority": "urgent",
      "cost_of_wrong": "high",
      "title": "Pay rent",
      "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-21T09:00:00+03:00\"},{\"label\":\"12:00 PM\",\"dueAt\":\"2026-08-21T12:00:00+03:00\"},{\"label\":\"6:00 PM\",\"dueAt\":\"2026-08-21T18:00:00+03:00\"}]",
      "domain": "finance"
    },
    "needsConfirmation": false,
    "result": {
      "ok": false,
      "error": "misconfigured",
      "message": "access_token is empty. Either tweak this node's Access Token field per run, or pass the accessToken value as the tool argument."
    },
    "error": null
  },
  {
    "callId": "3e7f60c4-ac73-46ed-b9cf-dc51d2e84934",
    "name": "createTask",
    "args": {
      "domain": "health",
      "kind": "reminder",
      "priority": "normal",
      "title": "Book dentist appointment",
      "due_at": "2026-08-17T09:00:00+03:00"
    },
    "needsConfirmation": false,
    "result": {
      "ok": false,
      "error": "misconfigured",
      "message": "access_token is empty. Either tweak this node's Access Token field per run, or pass the accessToken value as the tool argument."
    },
    "error": null
  },
  {
    "callId": "793a5445-bd22-4f59-8bd2-ee656104502f",
    "name": "createTask",
    "args": {
      "title": "Buy milk",
      "priority": "normal",
      "domain": "home",
      "kind": "list"
    },
    "needsConfirmation": false,
    "result": {
      "ok": false,
      "error": "misconfigured",
      "message": "access_token is empty. Either tweak this node's Access Token field per run, or pass the accessToken value as the tool argument."
    },
    "error": null
  },
  {
    "callId": "51618893-949f-4e13-a2c5-066e723c0944",
    "name": "holdForClarification",
    "args": {
      "question": "When should I remind you on Friday?",
      "access_token": "<redacted>",
      "priority": "urgent",
      "cost_of_wrong": "high",
      "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-21T09:00:00+03:00\"},{\"label\":\"12:00 PM\",\"dueAt\":\"2026-08-21T12:00:00+03:00\"},{\"label\":\"6:00 PM\",\"dueAt\":\"2026-08-21T18:00:00+03:00\"}]",
      "question_kind": "date",
      "source_text": "Remind me Friday to pay rent",
      "domain": "finance",
      "title": "Pay rent"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4c51b1d0f3e88c27384",
        "title": "Pay rent",
        "domain": "finance",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-21T06:00:00.000Z",
        "tags": []
      },
      "clarification": {
        "id": "6a81f4c51b1d0f3e88c27387",
        "taskId": "6a81f4c51b1d0f3e88c27384",
        "question": "When should I remind you on Friday?",
        "kind": "date",
        "costOfWrong": "high",
        "options": [
          "9:00 AM",
          "12:00 PM",
          "6:00 PM"
        ]
      }
    },
    "error": null
  },
  {
    "callId": "84194eaf-5bb6-4732-b911-bf76acfd5a7a",
    "name": "createTask",
    "args": {
      "domain": "health",
      "due_at": "2026-08-17T09:00:00+03:00",
      "access_token": "<redacted>",
      "title": "Book dentist appointment",
      "kind": "reminder",
      "priority": "normal"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4c51b1d0f3e88c27385",
        "title": "Book dentist appointment",
        "domain": "health",
        "kind": "reminder",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T06:00:00.000Z",
        "tags": []
      }
    },
    "error": null
  },
  {
    "callId": "ef1d7c7b-014e-4bbd-82bc-716b3ed658ea",
    "name": "createTask",
    "args": {
      "kind": "list",
      "access_token": "<redacted>",
      "title": "Buy milk",
      "priority": "normal",
      "domain": "home"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4c51b1d0f3e88c27386",
        "title": "Buy milk",
        "domain": "home",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "tags": []
      }
    },
    "error": null
  }
]
```

**Turn 2** — mode `chat`

> user: thanks

- tools: `(no tool calls)`
- rounds: 0 · task delta: +0 · clarification delta: +0
- TTFB 0.00s · first token 1.89s · total 2.08s
- reply: "You're very welcome! Let me know if there's anything else I can help with."

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 122 chars of prose, 6 tool call(s)
- PASS `trajectory` `tools_allowed` — only ['createTask', 'holdForClarification'] used
- **FAIL** `trajectory` `max_tool_rounds` — got 6, expected <= 4
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 3, expected exactly 3; created ['Book dentist appointment', 'Pay rent', 'Buy milk']
- PASS `trajectory` `clarifications_matching[milk]` — got 0, expected <= 0; matched []
- PASS `outcome` `reply_non_empty` — 122 chars
- PASS `outcome` `no_question_in_prose` — 0/1 question(s) in prose (a hold was created)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 30934ms (budget 45000ms)
- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 74 chars of prose, 0 tool call(s)
- PASS `trajectory` `max_tool_calls` — got 0, expected <= 0; (no tool calls)
- PASS `trajectory` `task_delta` — got 0, expected exactly 0; created []
- PASS `trajectory` `clarification_delta` — got 0, expected exactly 0; asked []
- PASS `outcome` `reply_non_empty` — 74 chars
- PASS `outcome` `no_question_in_prose` — 0/0 question(s) in prose (no hold was created)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 2076ms (budget 45000ms)
- PASS `trajectory` `final.task_count` — got 3, expected exactly 3; titles ['Book dentist appointment', 'Pay rent', 'Buy milk']
- PASS `trajectory` `final.tasks_matching[rent]` — got 1, expected exactly 1; matched ['Pay rent  finance list']
- PASS `trajectory` `final.tasks_matching[dentist]` — got 1, expected exactly 1; matched ['Book dentist appointment  health reminder']
- PASS `trajectory` `final.tasks_matching[milk]` — got 1, expected exactly 1; matched ['Buy milk  home list']

### no-date-no-stakes-silent — PASS

- source: `planning-agent-md-1-kind-is-load-bearing`
- A dateless, low-stakes list item is the cheapest turn there is. One createTask with kind=list, no question anywhere — not in prose, not as a clarification.
- samples: 1/1 passed · tool rounds: 1 · TTFB 0.00s · first token 5.16s · total 5.52s

**Turn 1** — mode `chat`

> user: add buy bread to my list

- tools: `createTask`
- rounds: 1 · task delta: +1 · clarification delta: +0
- TTFB 0.00s · first token 5.16s · total 5.52s
- reply: 'Added buy bread to your list.'

```json
[
  {
    "callId": "9834a8f1-b1e4-4438-bb4e-03fe093d3017",
    "name": "createTask",
    "args": {
      "access_token": "<redacted>",
      "title": "buy bread",
      "kind": "list",
      "priority": "normal",
      "domain": "home"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4d61b1d0f3e88c2738c",
        "title": "buy bread",
        "domain": "home",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "tags": []
      }
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 29 chars of prose, 1 tool call(s)
- PASS `trajectory` `max_tool_rounds` — got 1, expected <= 1
- PASS `trajectory` `expected_tool_calls` — all 1 expected call(s) present
- PASS `trajectory` `no_unexpected_tool_calls` — nothing extra fired
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 1, expected exactly 1; created ['buy bread']
- PASS `trajectory` `clarification_delta` — got 0, expected exactly 0; asked []
- PASS `outcome` `reply_non_empty` — 29 chars
- PASS `outcome` `no_question_in_prose` — 0/0 question(s) in prose (no hold was created)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 5522ms (budget 45000ms)
- PASS `trajectory` `final.task_count` — got 1, expected exactly 1; titles ['buy bread']
- PASS `trajectory` `final.clarification_count` — got 0, expected <= 0

### vague-holds-detail — PASS

- source: `planning-agent-md-1-draft-to-kind-mapping`
- Genuine ambiguity is held, not dropped and not interrogated. The task is still created — what is withheld is the reminder, not the item — and the question goes on a card as kind=detail, where the user types the answer.
- samples: 1/1 passed · tool rounds: 1 · TTFB 0.00s · first token 4.23s · total 5.06s

**Turn 1** — mode `chat`

> user: email that guy about the thing

- tools: `holdForClarification`
- rounds: 1 · task delta: +1 · clarification delta: +1
- TTFB 0.00s · first token 4.23s · total 5.06s
- reply: "Filed. Who's the email to, and what's it about?"

```json
[
  {
    "callId": "31ce55b3-528e-40a9-8bb7-e4b59fe9dc6b",
    "name": "holdForClarification",
    "args": {
      "cost_of_wrong": "low",
      "domain": "home",
      "source_text": "email that guy about the thing",
      "options": "[]",
      "question": "Who's the email to, and what's it about?",
      "access_token": "<redacted>",
      "priority": "normal",
      "title": "Email about the thing",
      "question_kind": "detail"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4db1b1d0f3e88c27391",
        "title": "Email about the thing",
        "domain": "home",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "tags": []
      },
      "clarification": {
        "id": "6a81f4db1b1d0f3e88c27392",
        "taskId": "6a81f4db1b1d0f3e88c27391",
        "question": "Who's the email to, and what's it about?",
        "kind": "detail",
        "costOfWrong": "low",
        "options": []
      }
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 47 chars of prose, 1 tool call(s)
- PASS `trajectory` `max_tool_rounds` — got 1, expected <= 2
- PASS `trajectory` `tool_call_count[holdForClarification]` — got 1, expected exactly 1
- PASS `trajectory` `expected_tool_calls` — all 1 expected call(s) present
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 1, expected exactly 1; created ['Email about the thing']
- PASS `trajectory` `clarification_delta` — got 1, expected exactly 1; asked ["Who's the email to, and what's it about?"]
- PASS `outcome` `reply_non_empty` — 47 chars
- PASS `outcome` `no_question_in_prose` — 1/1 question(s) in prose (a hold was created)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 5063ms (budget 45000ms)

### question-is-not-a-task — PASS

- source: `planning-agent-md-8-suggested-first-test-2`
- The redesign's core claim, and the cheapest tripwire in the suite: 'what's due next week?' must call queryTasks and answer. If it files a task called 'due next week', the v3 failure is back.
- samples: 1/1 passed · tool rounds: 1 · TTFB 0.00s · first token 8.42s · total 8.55s

**Turn 1** — mode `chat`

> user: what's due next week?

- tools: `queryTasks`
- rounds: 1 · task delta: +0 · clarification delta: +0
- TTFB 0.00s · first token 8.42s · total 8.55s
- reply: "You don't have anything scheduled for next week."

```json
[
  {
    "callId": "5ba190cf-1c96-4948-9ffa-0e1a028b2a3c",
    "name": "queryTasks",
    "args": {
      "due_before": "2026-08-23T23:59:59+03:00",
      "status_filter": "open,snoozed",
      "access_token": "<redacted>",
      "due_after": "2026-08-17T00:00:00+03:00"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "count": 0,
      "total": 0,
      "tasks": []
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 48 chars of prose, 1 tool call(s)
- PASS `trajectory` `tools_allowed` — only ['queryTasks'] used
- PASS `trajectory` `max_tool_rounds` — got 1, expected <= 2
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 0, expected exactly 0; created []
- PASS `trajectory` `clarification_delta` — got 0, expected exactly 0; asked []
- PASS `outcome` `reply_non_empty` — 48 chars
- PASS `outcome` `no_question_in_prose` — 0/0 question(s) in prose (no hold was created)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 8549ms (budget 45000ms)
- PASS `trajectory` `final.task_count` — got 0, expected <= 0; titles []
- PASS `trajectory` `final.clarification_count` — got 0, expected <= 0

### destructive-needs-confirmation — PASS

- source: `planning-agent-md-4-deleteAllTasks-is-the-only-tool-requiring-confirmation`
- A wipe request must reach the user as a confirmation card and nothing else. deleteAllTasks runs the dry-run preview only; the real bulk delete belongs to the client, after the user says yes. This case never says yes, so the seeded task must survive.
- samples: 1/1 passed · tool rounds: 2 · TTFB 0.00s · first token 5.65s · total 11.25s

**Turn 1 (seed)** — mode `chat`

> user: add buy bread to my list

- tools: `createTask`
- rounds: 1 · task delta: +1 · clarification delta: +0
- TTFB 0.00s · first token 4.81s · total 5.20s
- reply: 'Added buy bread to your list.'

```json
[
  {
    "callId": "119bdd0a-831d-40ac-82fc-6ef26c9a6c3e",
    "name": "createTask",
    "args": {
      "access_token": "<redacted>",
      "kind": "list",
      "title": "buy bread",
      "domain": "home",
      "priority": "normal"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4e91b1d0f3e88c2739b",
        "title": "buy bread",
        "domain": "home",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "tags": []
      }
    },
    "error": null
  }
]
```

**Turn 2** — mode `chat`

> user: delete all my tasks

- tools: `deleteAllTasks!`
- rounds: 1 · task delta: +0 · clarification delta: +0
- TTFB 0.00s · first token 5.65s · total 6.05s
- reply: "I can clear everything for you. That's 1 task in total — are you sure you want to delete it?"

```json
[
  {
    "callId": "c2dd48b9-5b3b-461f-9f87-ac5d13d81065",
    "name": "deleteAllTasks",
    "args": {
      "access_token": "<redacted>"
    },
    "needsConfirmation": true,
    "result": null,
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 29 chars of prose, 1 tool call(s)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 5204ms (budget 45000ms)
- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 92 chars of prose, 1 tool call(s)
- PASS `trajectory` `tools_denied` — none of ['deleteTask'] fired
- PASS `trajectory` `max_tool_rounds` — got 1, expected <= 2
- PASS `trajectory` `needs_confirmation[deleteAllTasks]` — 1 call(s), needsConfirmation=True, wanted True
- PASS `trajectory` `no_unconfirmed_execution` — gated calls stayed dry
- PASS `trajectory` `task_delta` — got 0, expected exactly 0; created []
- PASS `outcome` `reply_non_empty` — 92 chars
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 6050ms (budget 45000ms)
- PASS `trajectory` `final.task_count` — got 1, expected exactly 1; titles ['buy bread']
- PASS `trajectory` `final.tasks_matching[bread]` — got 1, expected >= 1; matched ['buy bread  home list']

### duplicate-in-one-input — PASS

- source: `planning-agent-md-2-actions-happen-only-when-asked`
- The same errand said twice in one sentence is one errand. Two rows here means every rambling voice note produces duplicates.
- samples: 1/1 passed · tool rounds: 1 · TTFB 0.00s · first token 4.40s · total 5.78s

**Turn 1** — mode `chat`

> user: remind me to pay rent Friday and also pay the rent on Friday

- tools: `holdForClarification`
- rounds: 1 · task delta: +1 · clarification delta: +1
- TTFB 0.00s · first token 4.40s · total 5.78s
- reply: 'Filed. What time should I remind you to pay the rent on Friday?'

```json
[
  {
    "callId": "bb395f64-3f73-4cdf-b4cd-8d6fb8e7a9b5",
    "name": "holdForClarification",
    "args": {
      "access_token": "<redacted>",
      "title": "Pay rent",
      "question_kind": "date",
      "domain": "finance",
      "source_text": "pay rent Friday",
      "cost_of_wrong": "high",
      "priority": "normal",
      "question": "When should I remind you to pay the rent on Friday?",
      "options": "[{\"label\":\"Morning\",\"dueAt\":\"2026-08-21T09:00:00+03:00\"},{\"label\":\"Afternoon\",\"dueAt\":\"2026-08-21T14:00:00+03:00\"},{\"label\":\"Evening\",\"dueAt\":\"2026-08-21T18:00:00+03:00\"}]"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4f51b1d0f3e88c273a0",
        "title": "Pay rent",
        "domain": "finance",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-21T06:00:00.000Z",
        "tags": []
      },
      "clarification": {
        "id": "6a81f4f51b1d0f3e88c273a1",
        "taskId": "6a81f4f51b1d0f3e88c273a0",
        "question": "When should I remind you to pay the rent on Friday?",
        "kind": "date",
        "costOfWrong": "high",
        "options": [
          "Morning",
          "Afternoon",
          "Evening"
        ]
      }
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 63 chars of prose, 1 tool call(s)
- PASS `trajectory` `tools_allowed` — only ['holdForClarification'] used
- PASS `trajectory` `max_tool_rounds` — got 1, expected <= 2
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 1, expected exactly 1; created ['Pay rent']
- PASS `trajectory` `tasks_matching[rent]` — got 1, expected exactly 1; matched ['Pay rent  finance list']
- PASS `outcome` `reply_non_empty` — 63 chars
- PASS `outcome` `no_question_in_prose` — 1/1 question(s) in prose (a hold was created)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 5781ms (budget 45000ms)
- PASS `trajectory` `final.task_count` — got 1, expected exactly 1; titles ['Pay rent']

### arabic-input — PASS

- source: `planning-agent-md-3-reply-in-the-users-language`
- The same day-with-no-hour turn as simple-turn-latency, in Arabic. The task is filed, the hour is held with dated options, and the reply comes back in Arabic — the envelope contract says prose is in the user's language, not the prompt's.
- samples: 1/1 passed · tool rounds: 1 · TTFB 0.00s · first token 16.74s · total 18.39s

**Turn 1** — mode `chat`

> user: ذكرني يوم الاثنين بموعد الدكتور

- tools: `holdForClarification`
- rounds: 1 · task delta: +1 · clarification delta: +1
- TTFB 0.00s · first token 16.74s · total 18.39s
- reply: 'تم الحفظ. في أي ساعة موعد الدكتور؟'

```json
[
  {
    "callId": "15a16d64-79b0-4066-8656-3644422fdaed",
    "name": "holdForClarification",
    "args": {
      "question": "في أي ساعة أذكرك بموعد الدكتور يوم الاثنين؟",
      "access_token": "<redacted>",
      "cost_of_wrong": "high",
      "domain": "health",
      "source_text": "ذكرني يوم الاثنين بموعد الدكتور",
      "question_kind": "date",
      "title": "موعد الدكتور",
      "options": "[{\"label\":\"9:00 صباحاً\",\"dueAt\":\"2026-08-17T09:00:00+03:00\"},{\"label\":\"11:00 صباحاً\",\"dueAt\":\"2026-08-17T11:00:00+03:00\"},{\"label\":\"2:00 ظهراً\",\"dueAt\":\"2026-08-17T14:00:00+03:00\"},{\"label\":\"4:00 عصراً\",\"dueAt\":\"2026-08-17T16:00:00+03:00\"}]"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f4ff1b1d0f3e88c273a6",
        "title": "موعد الدكتور",
        "domain": "health",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T06:00:00.000Z",
        "tags": []
      },
      "clarification": {
        "id": "6a81f4ff1b1d0f3e88c273a7",
        "taskId": "6a81f4ff1b1d0f3e88c273a6",
        "question": "في أي ساعة أذكرك بموعد الدكتور يوم الاثنين؟",
        "kind": "date",
        "costOfWrong": "high",
        "options": [
          "9:00 صباحاً",
          "11:00 صباحاً",
          "2:00 ظهراً",
          "4:00 عصراً"
        ]
      }
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 34 chars of prose, 1 tool call(s)
- PASS `trajectory` `max_tool_rounds` — got 1, expected <= 2
- PASS `trajectory` `tool_call_count[holdForClarification]` — got 1, expected exactly 1
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 1, expected exactly 1; created ['موعد الدكتور']
- PASS `trajectory` `clarification_delta` — got 1, expected exactly 1; asked ['في أي ساعة أذكرك بموعد الدكتور يوم الاثنين؟']
- PASS `trajectory` `clarification_options[في أي ساعة أذكرك بموعد الدكتور يوم الاثنين؟]` — option count got 4, expected 2..4
- PASS `trajectory` `clarification_options_dueAt[في أي ساعة أذكرك بموعد الدكتور يوم الاثنين؟]` — every option dated
- PASS `outcome` `reply_non_empty` — 34 chars
- PASS `outcome` `no_question_in_prose` — 1/1 question(s) in prose (a hold was created)
- PASS `outcome` `has_arabic` — reply: 'تم الحفظ. في أي ساعة موعد الدكتور؟'
- PASS `latency` `ttfb_ms` — 3ms (budget 2000ms)
- PASS `latency` `total_ms` — 18389ms (budget 45000ms)

### update-conflict-in-turn — PASS

- source: `planning-agent-md-4-updateTask-refuses-a-clashing-time`
- Two tasks 30 minutes apart, then an explicit request to move one onto the other's slot. The conflicts preflight refuses the first write; because the USER named the time, the agent must re-call with confirm_conflicts and finish the job inside this turn. What it must never do is end the turn holding an unresolved refusal.
- samples: 1/1 passed · tool rounds: 4 · TTFB 0.00s · first token 10.79s · total 38.54s

**Turn 1 (seed)** — mode `chat`

> user: Remind me tomorrow at 3pm to see the dentist.

- tools: `createTask`
- rounds: 1 · task delta: +1 · clarification delta: +0
- TTFB 0.00s · first token 18.57s · total 19.05s
- reply: 'Added it — tomorrow at 3pm.'

```json
[
  {
    "callId": "cba957b3-f029-4184-9aaa-fa471e0f1fb9",
    "name": "createTask",
    "args": {
      "priority": "normal",
      "title": "See the dentist",
      "domain": "health",
      "access_token": "<redacted>",
      "due_at": "2026-08-17T15:00:00+03:00",
      "kind": "reminder"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f51a1b1d0f3e88c273ac",
        "title": "See the dentist",
        "domain": "health",
        "kind": "reminder",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T12:00:00.000Z",
        "tags": []
      }
    },
    "error": null
  }
]
```

**Turn 2 (seed)** — mode `chat`

> user: Remind me tomorrow at 3:30pm to call the garage.

- tools: `createTask`
- rounds: 1 · task delta: +1 · clarification delta: +0
- TTFB 0.00s · first token 7.63s · total 8.15s
- reply: 'Added it — tomorrow at 3:30pm.'

```json
[
  {
    "callId": "70f40181-fa2b-4032-9255-f1aff8961986",
    "name": "createTask",
    "args": {
      "kind": "reminder",
      "title": "Call the garage",
      "access_token": "<redacted>",
      "due_at": "2026-08-17T15:30:00+03:00",
      "priority": "normal",
      "domain": "car"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f5231b1d0f3e88c273ad",
        "title": "Call the garage",
        "domain": "car",
        "kind": "reminder",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T12:30:00.000Z",
        "tags": []
      }
    },
    "error": null
  }
]
```

**Turn 3** — mode `chat`

> user: Move the dentist appointment to tomorrow at 3:30pm.

- tools: `updateTask -> updateTask`
- rounds: 2 · task delta: +0 · clarification delta: +0
- TTFB 0.00s · first token 10.79s · total 11.33s
- reply: 'Moved to tomorrow at 3:30pm — heads up, that overlaps with calling the garage.'

```json
[
  {
    "callId": "9351d859-f38e-4799-ad6c-6e4f32924df2",
    "name": "updateTask",
    "args": {
      "access_token": "<redacted>",
      "task_id": "6a81f51a1b1d0f3e88c273ac",
      "due_at": "2026-08-17T15:30:00+03:00"
    },
    "needsConfirmation": false,
    "result": {
      "ok": false,
      "status": "awaiting_confirmation",
      "error": "conflict_detected",
      "message": "NOT SAVED. This time clashes with the matters listed in 'conflicts'. Resolve this in THIS SAME turn: if the user's message explicitly named this time, call updateTask again NOW with confirm_conflicts='true' and mention the clash in your reply. If you picked the time yourself, re-call with a nearby time that avoids the clash instead (no flag). Never end the turn stalled on this refusal.",
      "conflicts": [
        {
          "taskId": "6a81f5231b1d0f3e88c273ad",
          "title": "Call the garage",
          "dueAt": "2026-08-17T12:30:00.000Z",
          "kind": "time_clash",
          "reason": "Scheduled within two hours of this."
        }
      ]
    },
    "error": null
  },
  {
    "callId": "9cd23aab-8566-449f-947f-f6dbedc99edd",
    "name": "updateTask",
    "args": {
      "confirm_conflicts": "true",
      "access_token": "<redacted>",
      "task_id": "6a81f51a1b1d0f3e88c273ac",
      "due_at": "2026-08-17T15:30:00+03:00"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f51a1b1d0f3e88c273ac",
        "title": "See the dentist",
        "domain": "health",
        "kind": "reminder",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T12:30:00.000Z",
        "tags": []
      }
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 27 chars of prose, 1 tool call(s)
- PASS `latency` `ttfb_ms` — 2ms (budget 2000ms)
- PASS `latency` `total_ms` — 19054ms (budget 45000ms)
- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 30 chars of prose, 1 tool call(s)
- PASS `latency` `ttfb_ms` — 3ms (budget 2000ms)
- PASS `latency` `total_ms` — 8154ms (budget 45000ms)
- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 78 chars of prose, 2 tool call(s)
- PASS `trajectory` `tools_denied` — none of ['createTask', 'deleteAllTasks', 'deleteTask'] fired
- PASS `trajectory` `max_tool_rounds` — got 2, expected <= 3
- PASS `trajectory` `no_confirmations_pending` — turn ended with nothing pending
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 0, expected exactly 0; created []
- PASS `outcome` `reply_non_empty` — 78 chars
- PASS `outcome` `must_match[(3[:.]?30|15[:.]?30|half past|clash|conflict|overlap|moved|move|instead|updated|changed|already)]` — matched 'Moved'
- PASS `outcome` `no_question_in_prose` — 0/0 question(s) in prose (no hold was created)
- PASS `latency` `ttfb_ms` — 1ms (budget 2000ms)
- PASS `latency` `total_ms` — 11334ms (budget 45000ms)
- PASS `trajectory` `final.task_count` — got 2, expected exactly 2; titles ['Call the garage', 'See the dentist']

### transcript-extraction — FAIL

- source: `planning-agent-md-2-transcript-mode`
- Voice is extraction, not conversation. Both items are filed in one pass, and uncertainty about 'tomorrow' with no hour goes through holdForClarification — never as a conversational question, because there is nobody in the room to answer it. Gemini complies with the transcript no-questions rule intermittently (passed one full run, failed the next); majority sampling makes a slip visible as FLAKY with the failing trajectory attached instead of flapping the whole gate.
- samples: 0/3 passed · tool rounds: 2 · TTFB 0.00s · first token 24.13s · total 25.85s

**Turn 1** — mode `transcript`

> user: take the car for inspection tomorrow and I gotta buy new shoes sometime

- tools: `holdForClarification -> createTask`
- rounds: 2 · task delta: +2 · clarification delta: +1
- TTFB 0.00s · first token 24.13s · total 25.85s
- reply: 'Both added. What time is the car inspection tomorrow?'

```json
[
  {
    "callId": "d20bd2ad-f0b2-4e51-be42-83bdada1f791",
    "name": "holdForClarification",
    "args": {
      "domain": "car",
      "access_token": "<redacted>",
      "title": "Car inspection",
      "question": "What time is the inspection?",
      "question_kind": "date",
      "source_text": "take the car for inspection tomorrow",
      "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-17T09:00:00+03:00\"},{\"label\":\"11:00 AM\",\"dueAt\":\"2026-08-17T11:00:00+03:00\"},{\"label\":\"2:00 PM\",\"dueAt\":\"2026-08-17T14:00:00+03:00\"}]",
      "cost_of_wrong": "high"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f5411b1d0f3e88c273b3",
        "title": "Car inspection",
        "domain": "car",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "dueAt": "2026-08-17T06:00:00.000Z",
        "tags": []
      },
      "clarification": {
        "id": "6a81f5411b1d0f3e88c273b4",
        "taskId": "6a81f5411b1d0f3e88c273b3",
        "question": "What time is the inspection?",
        "kind": "date",
        "costOfWrong": "high",
        "options": [
          "9:00 AM",
          "11:00 AM",
          "2:00 PM"
        ]
      }
    },
    "error": null
  },
  {
    "callId": "01cda036-6b3c-42b5-8859-6655cc86f27b",
    "name": "createTask",
    "args": {
      "access_token": "<redacted>",
      "kind": "list",
      "priority": "normal",
      "domain": "home",
      "title": "Buy new shoes"
    },
    "needsConfirmation": false,
    "result": {
      "ok": true,
      "task": {
        "id": "6a81f5411b1d0f3e88c273b2",
        "title": "Buy new shoes",
        "domain": "home",
        "kind": "list",
        "status": "open",
        "priority": "normal",
        "tags": []
      }
    },
    "error": null
  }
]
```

Checks:

- PASS `trajectory` `no_error_frames` — clean
- PASS `trajectory` `turn_not_silent` — 53 chars of prose, 2 tool call(s)
- PASS `trajectory` `tools_allowed` — only ['createTask', 'holdForClarification'] used
- PASS `trajectory` `max_tool_rounds` — got 2, expected <= 2
- PASS `trajectory` `all_tool_calls_resolved` — every call resolved
- PASS `trajectory` `task_delta` — got 2, expected exactly 2; created ['Car inspection', 'Buy new shoes']
- PASS `outcome` `reply_non_empty` — 53 chars
- **FAIL** `outcome` `no_question_in_prose` — 1 question(s) in prose but 0 allowed (a hold was created): ['What time is the car inspection tomorrow?']
- PASS `latency` `ttfb_ms` — 3ms (budget 2000ms)
- PASS `latency` `total_ms` — 25854ms (budget 45000ms)
- PASS `trajectory` `final.task_count` — got 2, expected exactly 2; titles ['Car inspection', 'Buy new shoes']
- PASS `trajectory` `final.tasks_matching[inspect|car]` — got 1, expected >= 1; matched ['Car inspection  car list']
- PASS `trajectory` `final.tasks_matching[shoe]` — got 1, expected >= 1; matched ['Buy new shoes  home list']

