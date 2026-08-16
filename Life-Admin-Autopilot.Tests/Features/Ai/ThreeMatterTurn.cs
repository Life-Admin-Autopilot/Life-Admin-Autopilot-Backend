namespace Life_Admin_Autopilot.Tests.Features.Ai;

using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// <b>One real Langflow turn, kept verbatim.</b>
///
/// <para>
/// Captured off a live Langflow 1.11.2 answering <i>"add buy milk, buy bread, and buy
/// eggs to my list"</i> through the <c>PlanningInput-v4</c> tweak binding, on a
/// throwaway account, against the backend the flow's tools call back into. The agent
/// made three <c>createTask</c> calls and the store gained three matters.
/// </para>
///
/// <para>
/// <b>What was changed, and nothing else.</b> Bearer tokens are redacted, LangChain and
/// message uuids are shortened to <c>id-n</c>, Mongo ids to <c>task-n</c>, the frames
/// that carry no tool activity (<c>token</c>, <c>build_start</c>, <c>build_end</c>, the
/// agent's own <c>chain_start</c>) are omitted, and three of the six identical trailing
/// redeliveries are elided. Ordering, nesting, key names and the redelivery pattern are
/// exactly as they arrived.
/// </para>
///
/// <para>
/// <b>Read the sequence, because it IS the bug.</b> Three <c>chain_start</c> log frames
/// announce three distinct invocations with three distinct ids. The
/// <c>add_message</c> rows that follow never carry more than TWO <c>tool_use</c>
/// blocks: at line 25 block 0 is <c>buy milk</c>, and at line 37 the SAME block, in the
/// SAME message id, is <c>buy bread</c> — Langflow keeps one block per tool NAME and
/// rewrites it on the next call to that tool. The output that eventually lands on
/// block 0 is <c>buy bread</c>'s. Anything that reads the block's position as an
/// invocation identity therefore announces two calls for three and hands one of them
/// another call's result.
/// </para>
///
/// <para>
/// Note also that the outcomes arrive in a different order than the calls — milk,
/// eggs, bread — so pairing by arrival order is no better than pairing by position.
/// </para>
/// </summary>
internal static class ThreeMatterTurn
{
    /// <summary>
    /// The whole captured turn, in wire order. Parsed on first use rather than in a
    /// field initializer, because <c>Lines</c> is declared below it and static
    /// initializers run in declaration order.
    /// </summary>
    public static IReadOnlyList<LangflowFrame> Frames => _frames ??= Parse(Lines);

    private static IReadOnlyList<LangflowFrame>? _frames;

    /// <summary>A single <c>chain_start</c> frame, for the reader's own tests.</summary>
    public const string CallsBuyMilk = Line11;

    /// <summary>A single <c>tool_end</c> frame, for the reader's own tests.</summary>
    public const string EndsBuyMilk = Line33;

    /// <summary>Line 11 — log / chain_start — the agent calls createTask("buy milk"), id id-1</summary>
    private const string Line11 =
        """
        {
         "event": "log",
         "data": {
          "component_id": "PlanningAgent-v4",
          "message": {
           "type": "chain_start",
           "name": "tools",
           "inputs": [
            {
             "name": "createTask",
             "args": {
              "kind": "list",
              "title": "buy milk",
              "access_token": "<redacted access token>",
              "priority": "normal",
              "domain": "home"
             },
             "id": "id-1",
             "type": "tool_call"
            }
           ]
          }
         }
        }
        """;

    /// <summary>Line 13 — log / chain_start — the agent calls createTask("buy bread"), id id-2</summary>
    private const string Line13 =
        """
        {
         "event": "log",
         "data": {
          "component_id": "PlanningAgent-v4",
          "message": {
           "type": "chain_start",
           "name": "tools",
           "inputs": [
            {
             "name": "createTask",
             "args": {
              "domain": "home",
              "priority": "normal",
              "title": "buy bread",
              "kind": "list",
              "access_token": "<redacted access token>"
             },
             "id": "id-2",
             "type": "tool_call"
            }
           ]
          }
         }
        }
        """;

    /// <summary>Line 15 — log / chain_start — the agent calls createTask("buy eggs"), id id-3</summary>
    private const string Line15 =
        """
        {
         "event": "log",
         "data": {
          "component_id": "PlanningAgent-v4",
          "message": {
           "type": "chain_start",
           "name": "tools",
           "inputs": [
            {
             "name": "createTask",
             "args": {
              "access_token": "<redacted access token>",
              "domain": "home",
              "kind": "list",
              "title": "buy eggs",
              "priority": "normal"
             },
             "id": "id-3",
             "type": "tool_call"
            }
           ]
          }
         }
        }
        """;

    /// <summary>Line 23 — add_message — blocks now hold [buy milk]</summary>
    private const string Line23 =
        """
        {
         "event": "add_message",
         "data": {
          "id": "id-4",
          "text": "",
          "content_blocks": [
           {
            "title": "Agent Steps",
            "contents": [
             {
              "type": "tool_use",
              "duration": 19,
              "header": {
               "title": "Accessing **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "kind": "list",
               "title": "buy milk",
               "access_token": "<redacted access token>",
               "priority": "normal",
               "domain": "home"
              },
              "output": null,
              "error": null
             }
            ],
            "allow_markdown": true,
            "media_url": null
           }
          ]
         }
        }
        """;

    /// <summary>Line 25 — add_message — blocks now hold [buy milk, buy eggs]</summary>
    private const string Line25 =
        """
        {
         "event": "add_message",
         "data": {
          "id": "id-4",
          "text": "",
          "content_blocks": [
           {
            "title": "Agent Steps",
            "contents": [
             {
              "type": "tool_use",
              "duration": 19,
              "header": {
               "title": "Accessing **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "kind": "list",
               "title": "buy milk",
               "access_token": "<redacted access token>",
               "priority": "normal",
               "domain": "home"
              },
              "output": null,
              "error": null
             },
             {
              "type": "tool_use",
              "duration": 22,
              "header": {
               "title": "Accessing **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "access_token": "<redacted access token>",
               "domain": "home",
               "kind": "list",
               "title": "buy eggs",
               "priority": "normal"
              },
              "output": null,
              "error": null
             }
            ],
            "allow_markdown": true,
            "media_url": null
           }
          ]
         }
        }
        """;

    /// <summary>Line 33 — log / tool_end — createTask "buy milk" came back, for id id-1</summary>
    private const string Line33 =
        """
        {
         "event": "log",
         "data": {
          "component_id": "PlanningAgent-v4",
          "message": {
           "type": "tool_end",
           "output": {
            "content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-1\\\", \\\"title\\\": \\\"buy milk\\\", \\\"domain\\\": \\\"home\\\", \\\"kind\\\": \\\"list\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"tags\\\": []}}\"}",
            "type": "tool",
            "name": "createTask",
            "tool_call_id": "id-1",
            "artifact": {
             "value": "{\"ok\": true, \"task\": {\"id\": \"task-1\", \"title\": \"buy milk\", \"domain\": \"home\", \"kind\": \"list\", \"status\": \"open\", \"priority\": \"normal\", \"tags\": []}}"
            },
            "status": "success"
           }
          }
         }
        }
        """;

    /// <summary>Line 37 — add_message — blocks now hold [buy bread, buy eggs]</summary>
    private const string Line37 =
        """
        {
         "event": "add_message",
         "data": {
          "id": "id-4",
          "text": "",
          "content_blocks": [
           {
            "title": "Agent Steps",
            "contents": [
             {
              "type": "tool_use",
              "duration": 19,
              "header": {
               "title": "Accessing **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "domain": "home",
               "priority": "normal",
               "title": "buy bread",
               "kind": "list",
               "access_token": "<redacted access token>"
              },
              "output": null,
              "error": null
             },
             {
              "type": "tool_use",
              "duration": 22,
              "header": {
               "title": "Accessing **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "access_token": "<redacted access token>",
               "domain": "home",
               "kind": "list",
               "title": "buy eggs",
               "priority": "normal"
              },
              "output": null,
              "error": null
             }
            ],
            "allow_markdown": true,
            "media_url": null
           }
          ]
         }
        }
        """;

    /// <summary>Line 43 — log / tool_end — createTask "buy eggs" came back, for id id-3</summary>
    private const string Line43 =
        """
        {
         "event": "log",
         "data": {
          "component_id": "PlanningAgent-v4",
          "message": {
           "type": "tool_end",
           "output": {
            "content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-2\\\", \\\"title\\\": \\\"buy eggs\\\", \\\"domain\\\": \\\"home\\\", \\\"kind\\\": \\\"list\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"tags\\\": []}}\"}",
            "type": "tool",
            "name": "createTask",
            "tool_call_id": "id-3",
            "artifact": {
             "value": "{\"ok\": true, \"task\": {\"id\": \"task-2\", \"title\": \"buy eggs\", \"domain\": \"home\", \"kind\": \"list\", \"status\": \"open\", \"priority\": \"normal\", \"tags\": []}}"
            },
            "status": "success"
           }
          }
         }
        }
        """;

    /// <summary>Line 45 — log / tool_end — createTask "buy bread" came back, for id id-2</summary>
    private const string Line45 =
        """
        {
         "event": "log",
         "data": {
          "component_id": "PlanningAgent-v4",
          "message": {
           "type": "tool_end",
           "output": {
            "content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-3\\\", \\\"title\\\": \\\"buy bread\\\", \\\"domain\\\": \\\"home\\\", \\\"kind\\\": \\\"list\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"tags\\\": []}}\"}",
            "type": "tool",
            "name": "createTask",
            "tool_call_id": "id-2",
            "artifact": {
             "value": "{\"ok\": true, \"task\": {\"id\": \"task-3\", \"title\": \"buy bread\", \"domain\": \"home\", \"kind\": \"list\", \"status\": \"open\", \"priority\": \"normal\", \"tags\": []}}"
            },
            "status": "success"
           }
          }
         }
        }
        """;

    /// <summary>Line 47 — add_message — blocks now hold [buy bread, buy eggs]</summary>
    private const string Line47 =
        """
        {
         "event": "add_message",
         "data": {
          "id": "id-4",
          "text": "",
          "content_blocks": [
           {
            "title": "Agent Steps",
            "contents": [
             {
              "type": "tool_use",
              "duration": 19,
              "header": {
               "title": "Accessing **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "domain": "home",
               "priority": "normal",
               "title": "buy bread",
               "kind": "list",
               "access_token": "<redacted access token>"
              },
              "output": null,
              "error": null
             },
             {
              "type": "tool_use",
              "duration": 22,
              "header": {
               "title": "Accessing **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "access_token": "<redacted access token>",
               "domain": "home",
               "kind": "list",
               "title": "buy eggs",
               "priority": "normal"
              },
              "output": null,
              "error": null
             }
            ],
            "allow_markdown": true,
            "media_url": null
           }
          ]
         }
        }
        """;

    /// <summary>Line 49 — add_message — blocks now hold [buy bread, buy eggs+out]</summary>
    private const string Line49 =
        """
        {
         "event": "add_message",
         "data": {
          "id": "id-4",
          "text": "",
          "content_blocks": [
           {
            "title": "Agent Steps",
            "contents": [
             {
              "type": "tool_use",
              "duration": 19,
              "header": {
               "title": "Accessing **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "domain": "home",
               "priority": "normal",
               "title": "buy bread",
               "kind": "list",
               "access_token": "<redacted access token>"
              },
              "output": null,
              "error": null
             },
             {
              "type": "tool_use",
              "duration": 18,
              "header": {
               "title": "Executed **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "access_token": "<redacted access token>",
               "domain": "home",
               "kind": "list",
               "title": "buy eggs",
               "priority": "normal"
              },
              "output": {
               "content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-2\\\", \\\"title\\\": \\\"buy eggs\\\", \\\"domain\\\": \\\"home\\\", \\\"kind\\\": \\\"list\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"tags\\\": []}}\"}",
               "additional_kwargs": {},
               "response_metadata": {},
               "type": "tool",
               "name": "createTask",
               "id": null,
               "tool_call_id": "id-3",
               "artifact": {
                "value": "{\"ok\": true, \"task\": {\"id\": \"task-2\", \"title\": \"buy eggs\", \"domain\": \"home\", \"kind\": \"list\", \"status\": \"open\", \"priority\": \"normal\", \"tags\": []}}"
               },
               "status": "success"
              },
              "error": null
             }
            ],
            "allow_markdown": true,
            "media_url": null
           }
          ]
         }
        }
        """;

    /// <summary>Line 81 — add_message — blocks now hold [buy bread+out, buy eggs+out] + the envelope</summary>
    private const string Line81 =
        """
        {
         "event": "add_message",
         "data": {
          "id": "id-4",
          "text": "{\n  \"mode\": \"chat\",\n  \"reply\": \"Added all three to your list.\",\n  \"tasks\": [\n    {\n      \"id\": \"task-1\",\n      \"action\": \"created\",\n      \"title\": \"buy milk\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-3\",\n      \"action\": \"created\",\n      \"title\": \"buy bread\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-2\",\n      \"action\": \"created\",\n      \"title\": \"buy eggs\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    }\n  ],\n  \"clarifications\": [],\n  \"pendingConfirmations\": []\n}",
          "content_blocks": [
           {
            "title": "Agent Steps",
            "contents": [
             {
              "type": "tool_use",
              "duration": 19,
              "header": {
               "title": "Executed **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "domain": "home",
               "priority": "normal",
               "title": "buy bread",
               "kind": "list",
               "access_token": "<redacted access token>"
              },
              "output": {
               "content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-3\\\", \\\"title\\\": \\\"buy bread\\\", \\\"domain\\\": \\\"home\\\", \\\"kind\\\": \\\"list\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"tags\\\": []}}\"}",
               "additional_kwargs": {},
               "response_metadata": {},
               "type": "tool",
               "name": "createTask",
               "id": "id-5",
               "tool_call_id": "id-2",
               "artifact": {
                "value": "{\"ok\": true, \"task\": {\"id\": \"task-3\", \"title\": \"buy bread\", \"domain\": \"home\", \"kind\": \"list\", \"status\": \"open\", \"priority\": \"normal\", \"tags\": []}}"
               },
               "status": "success"
              },
              "error": null
             },
             {
              "type": "tool_use",
              "duration": 18,
              "header": {
               "title": "Executed **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "access_token": "<redacted access token>",
               "domain": "home",
               "kind": "list",
               "title": "buy eggs",
               "priority": "normal"
              },
              "output": {
               "content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-2\\\", \\\"title\\\": \\\"buy eggs\\\", \\\"domain\\\": \\\"home\\\", \\\"kind\\\": \\\"list\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"tags\\\": []}}\"}",
               "additional_kwargs": {},
               "response_metadata": {},
               "type": "tool",
               "name": "createTask",
               "id": null,
               "tool_call_id": "id-3",
               "artifact": {
                "value": "{\"ok\": true, \"task\": {\"id\": \"task-2\", \"title\": \"buy eggs\", \"domain\": \"home\", \"kind\": \"list\", \"status\": \"open\", \"priority\": \"normal\", \"tags\": []}}"
               },
               "status": "success"
              },
              "error": null
             },
             {
              "type": "text",
              "duration": 1,
              "header": {},
              "text": "{\n  \"mode\": \"chat\",\n  \"reply\": \"Added all three to your list.\",\n  \"tasks\": [\n    {\n      \"id\": \"task-1\",\n      \"action\": \"created\",\n      \"title\": \"buy milk\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-3\",\n      \"action\": \"created\",\n      \"title\": \"buy bread\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-2\",\n      \"action\": \"created\",\n      \"title\": \"buy eggs\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    }\n  ],\n  \"clarifications\": [],\n  \"pendingConfirmations\": []\n}"
             }
            ],
            "allow_markdown": true,
            "media_url": null
           }
          ]
         }
        }
        """;

    /// <summary>Line 91 — add_message — blocks now hold [buy bread+out, buy eggs+out] + the envelope</summary>
    private const string Line91 =
        """
        {
         "event": "add_message",
         "data": {
          "id": "id-4",
          "text": "{\n  \"mode\": \"chat\",\n  \"reply\": \"Added all three to your list.\",\n  \"tasks\": [\n    {\n      \"id\": \"task-1\",\n      \"action\": \"created\",\n      \"title\": \"buy milk\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-3\",\n      \"action\": \"created\",\n      \"title\": \"buy bread\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-2\",\n      \"action\": \"created\",\n      \"title\": \"buy eggs\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    }\n  ],\n  \"clarifications\": [],\n  \"pendingConfirmations\": []\n}",
          "content_blocks": [
           {
            "title": "Agent Steps",
            "contents": [
             {
              "type": "tool_use",
              "duration": 19,
              "header": {
               "title": "Executed **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "domain": "home",
               "priority": "normal",
               "title": "buy bread",
               "kind": "list",
               "access_token": "<redacted access token>"
              },
              "output": {
               "content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-3\\\", \\\"title\\\": \\\"buy bread\\\", \\\"domain\\\": \\\"home\\\", \\\"kind\\\": \\\"list\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"tags\\\": []}}\"}",
               "additional_kwargs": {},
               "response_metadata": {},
               "type": "tool",
               "name": "createTask",
               "id": "id-5",
               "tool_call_id": "id-2",
               "artifact": {
                "value": "{\"ok\": true, \"task\": {\"id\": \"task-3\", \"title\": \"buy bread\", \"domain\": \"home\", \"kind\": \"list\", \"status\": \"open\", \"priority\": \"normal\", \"tags\": []}}"
               },
               "status": "success"
              },
              "error": null
             },
             {
              "type": "tool_use",
              "duration": 18,
              "header": {
               "title": "Executed **createTask**",
               "icon": "Hammer"
              },
              "name": "createTask",
              "tool_input": {
               "access_token": "<redacted access token>",
               "domain": "home",
               "kind": "list",
               "title": "buy eggs",
               "priority": "normal"
              },
              "output": {
               "content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-2\\\", \\\"title\\\": \\\"buy eggs\\\", \\\"domain\\\": \\\"home\\\", \\\"kind\\\": \\\"list\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"tags\\\": []}}\"}",
               "additional_kwargs": {},
               "response_metadata": {},
               "type": "tool",
               "name": "createTask",
               "id": null,
               "tool_call_id": "id-3",
               "artifact": {
                "value": "{\"ok\": true, \"task\": {\"id\": \"task-2\", \"title\": \"buy eggs\", \"domain\": \"home\", \"kind\": \"list\", \"status\": \"open\", \"priority\": \"normal\", \"tags\": []}}"
               },
               "status": "success"
              },
              "error": null
             },
             {
              "type": "text",
              "duration": 1,
              "header": {},
              "text": "{\n  \"mode\": \"chat\",\n  \"reply\": \"Added all three to your list.\",\n  \"tasks\": [\n    {\n      \"id\": \"task-1\",\n      \"action\": \"created\",\n      \"title\": \"buy milk\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-3\",\n      \"action\": \"created\",\n      \"title\": \"buy bread\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-2\",\n      \"action\": \"created\",\n      \"title\": \"buy eggs\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    }\n  ],\n  \"clarifications\": [],\n  \"pendingConfirmations\": []\n}"
             },
             {
              "type": "text",
              "duration": null,
              "header": {
               "title": "Output",
               "icon": "MessageSquare"
              },
              "text": "{\n  \"mode\": \"chat\",\n  \"reply\": \"Added all three to your list.\",\n  \"tasks\": [\n    {\n      \"id\": \"task-1\",\n      \"action\": \"created\",\n      \"title\": \"buy milk\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-3\",\n      \"action\": \"created\",\n      \"title\": \"buy bread\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    },\n    {\n      \"id\": \"task-2\",\n      \"action\": \"created\",\n      \"title\": \"buy eggs\",\n      \"domain\": \"home\",\n      \"kind\": \"list\",\n      \"priority\": \"normal\",\n      \"dueAt\": null\n    }\n  ],\n  \"clarifications\": [],\n  \"pendingConfirmations\": []\n}"
             }
            ],
            "allow_markdown": true,
            "media_url": null
           }
          ]
         }
        }
        """;

    private static readonly string[] Lines =
    {
        Line11,
        Line13,
        Line15,
        Line23,
        Line25,
        Line33,
        Line37,
        Line43,
        Line45,
        Line47,
        Line49,
        Line81,
        Line91,
    };

    /// <summary>
    /// Through the real parser, so the fixture proves the whole path from a physical
    /// line rather than starting halfway down it. Newlines are collapsed because the
    /// frames are pretty-printed here for reading and arrive on one line on the wire.
    /// </summary>
    private static IReadOnlyList<LangflowFrame> Parse(IEnumerable<string> lines)
    {
        var frames = new List<LangflowFrame>();

        foreach (var line in lines)
        {
            string? pending = null;
            if (LangflowWireContract.TryParseLine(line.ReplaceLineEndings(" "), ref pending, out var frame))
            {
                frames.Add(frame);
            }
        }

        return frames;
    }
}
