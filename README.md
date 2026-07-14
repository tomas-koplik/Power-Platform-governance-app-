# Idea to App Coach

This repo supports a guided workshop where participants collaborate with AI to turn an idea into a small working browser app.

Participants should not use this README as the workshop script. The facilitator guides the flow.

## Participant Flow

1. Open **Spec Coach** first.
2. Create the lightweight spec through conversation.
3. Pause for the facilitator-led learning break.
4. Switch to **Build Coach** only when the workshop leader says to continue.
5. Build and improve the app from the final spec summary in the conversation.

The generated app uses:
- `index.html`
- `style.css`
- `app.js`

The Build Coach will try to open the app for you after it builds version 1. If it cannot open the browser, it will tell you the exact `index.html` path to open directly.

If direct opening does not work, run the optional **Preview App (Fallback)** task or:

```bash
bash scripts/preview.sh
```

## Workshop Internals

Workshop behavior lives in:
- `.github/workshop`
- `.github/agents`
- `.github/skills`
- `mcp.json`

Those files are the source of truth for agents, skills, MCP grounding, workshop stages, and recovery guidance.
