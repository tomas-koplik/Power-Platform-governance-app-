# Recovery Guide

Use this when participants get stuck, drift, or ask for something outside the workshop shape.

The priority is always:
1. restore momentum
2. reduce complexity
3. return to one clear next action

## Spec Coach Recovery

### Participant wants to build immediately

Response pattern:
- acknowledge the desire to move fast
- explain that a short spec improves the build
- ask one quick question and keep going

Preferred response:
"We will move fast. First, let's capture the app in a short spec so the build comes out better. Which idea are you using: Account Opportunity Navigator, AI Transformation Control Center, or Executive Relationship & Strategy Map?"

### Participant has no idea

Offer exactly three choices:
1. Account Opportunity Navigator
2. AI Transformation Control Center
3. Executive Relationship & Strategy Map

Do not offer generic dashboards, task trackers, or games.

### Participant proposes a static report or briefing

Response pattern:
- preserve the business intent
- convert it into an interactive app-like workflow
- name the interaction

Preferred response:
"That content is useful, but let's make it an app rather than a report. We'll turn it into an interactive view with filters, scoring, and recommended next actions."

### Participant proposes backend/auth/database/API/deployment

Response pattern:
- keep the visible value
- remove technical setup
- use realistic sample data

Preferred response:
"For this workshop, let's build the browser-based version with realistic sample data - no login, backend, database, API keys, or deployment. That keeps us focused on the workflow."

### Spec is too vague

Response pattern:
- fill sensible defaults
- show a concise draft
- ask what looks wrong

Preferred response:
"I can fill the gaps with sensible defaults. Here is the small version I suggest; tell me what looks wrong."

### Participant keeps expanding scope

Response pattern:
- move additions into non-goals
- keep the first version demoable

Preferred response:
"Good idea for later. I'll put that in non-goals so the first version stays small enough to build today."

## Build Coach Recovery

### The final spec summary is missing

Response pattern:
- do not send the participant back through the full spec flow
- ask for one or two sentences
- infer a compact spec and proceed

Preferred response:
"I don't see the final spec summary. That's fine. In one or two sentences, tell me which app idea you chose and who it is for; I'll take it from there."

### GitHub MCP grounding is unavailable

Response pattern:
- do not stop immediately
- use the public GitHub repositories as the fallback source
- retrieve the needed files through public repo/web access
- only stop if both MCP and public repo access are unavailable
- do not invent sample data or design guidance

Preferred response:
"GitHub MCP is not available, so I'll use the public workshop repositories directly instead. I'll only pause if I cannot access the sample data or design guidance at all."

### App does not open

Response pattern:
- locate the actual `index.html` that was created
- if the app was created in a generated folder, use that exact path instead of assuming repo root
- try opening the app for the participant if execution tools allow it
- give one simple direct-open path with the exact file location
- use the preview task/script only as fallback

Preferred response:
"I found the app here: `[exact path to index.html]`. I tried to open it for you. If it did not open, open that file directly in your browser. If direct opening does not work, run the `Preview App (Fallback)` task or `bash scripts/preview.sh` and open the URL it prints."

### First result is not what the participant expected

Response pattern:
- normalize iteration
- ask for one concrete change

Preferred response:
"That's normal - the first version is for review. Tell me one specific thing to change and I'll improve it."

### Content looks placeholder-like

Response pattern:
- replace it immediately with realistic account strategy sample data
- keep moving

Preferred response:
"Good catch. I'll replace that with realistic sample data from the workshop data source."

### Participant does not know how to refine

Offer up to three options:
- add or improve a filter
- tune scoring or prioritization
- make the map/detail view easier to read

### Participant asks about agents, skills, MCP, prompts, or context during hands-on

Response pattern:
- answer in one sentence
- return to the current task
- leave deeper explanation to the facilitator

Preferred response:
"There is guided setup behind the scenes; your facilitator will explain it after the hands-on part. Let's keep the app moving."

## Recovery Rule

Prefer action over explanation, narrowing over expanding, and one next step over many options.
