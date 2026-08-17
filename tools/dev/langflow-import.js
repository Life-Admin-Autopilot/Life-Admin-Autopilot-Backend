const fs = require('fs');
const path = require('path');

// Load env
const envPath = path.join(__dirname, '../../.env');
const envContent = fs.readFileSync(envPath, 'utf8');
const env = {};
envContent.split(/\r?\n/).forEach(line => {
  const trimmed = line.trim();
  if (trimmed && !trimmed.startsWith('#')) {
    const match = trimmed.match(/^([^=]+)=(.*)$/);
    if (match) {
      const key = match[1].trim();
      let val = match[2].trim();
      if ((val.startsWith('"') && val.endsWith('"')) || (val.startsWith("'") && val.endsWith("'"))) {
        val = val.substring(1, val.length - 1);
      }
      env[key] = val;
    }
  }
});

const base = env.LANGFLOW_BASE_URL || 'http://127.0.0.1:7860';
const flowId = env.LANGFLOW_FLOW_ID || '6b0f1c2e-9a41-4d3f-8c77-91a1f10a9e14';
const key = env.GEMINI_API_KEY || env.EMBEDDINGS_API_KEY;
const stewardUrl = env.STEWARD_API_BASE_URL || 'http://host.docker.internal:4000';

async function run() {
  console.log(`Checking Langflow at ${base}`);
  try {
    const health = await fetch(`${base}/health`);
    if (!health.ok) throw new Error(`Health returned ${health.status}`);
  } catch (e) {
    console.error(`Langflow is not reachable at ${base}. Make sure container is up.`);
    process.exit(1);
  }

  // Auto login
  console.log('Logging in to Langflow...');
  const loginRes = await fetch(`${base}/api/v1/auto_login`, { method: 'GET' });
  if (!loginRes.ok) {
    const errText = await loginRes.text();
    console.error(`Auto login failed with status ${loginRes.status}: ${errText}`);
    process.exit(1);
  }
  const loginData = await loginRes.json();
  const token = loginData.access_token;
  const headers = { Authorization: `Bearer ${token}` };

  // Check flow
  console.log(`Checking if flow ${flowId} exists...`);
  const flowCheck = await fetch(`${base}/api/v1/flows/${flowId}`, { headers });
  if (flowCheck.status === 200) {
    console.log('Flow already present — leaving it alone.');
  } else {
    console.log('Importing flow file...');
    const flowFilePath = path.join(__dirname, '../../langflow/planning-agent.v4.json');
    const flowFileContent = fs.readFileSync(flowFilePath, 'utf8');

    // Natively build FormData
    const formData = new FormData();
    const blob = new Blob([flowFileContent], { type: 'application/json' });
    formData.append('file', blob, 'planning-agent.v4.json');

    const uploadRes = await fetch(`${base}/api/v1/flows/upload/`, {
      method: 'POST',
      headers,
      body: formData
    });

    if (!uploadRes.ok) {
      const errText = await uploadRes.text();
      console.error(`Import failed: ${errText}`);
      process.exit(1);
    }
    console.log('Flow file imported successfully!');
  }

  if (!key) {
    console.log('\nNo GEMINI_API_KEY or EMBEDDINGS_API_KEY found in .env — flow imported but has no key.');
    return;
  }

  // Manage variables helper
  async function setVariable(name, value, type, field) {
    // Get existing variables
    const varsRes = await fetch(`${base}/api/v1/variables/`, { headers });
    if (!varsRes.ok) {
      console.error(`Failed to list variables`);
      return;
    }
    const vars = await varsRes.json();
    const existing = vars.find(v => v.name === name);
    if (existing) {
      console.log(`Deleting existing variable ${name}...`);
      await fetch(`${base}/api/v1/variables/${existing.id}`, {
        method: 'DELETE',
        headers
      });
    }

    // Create new variable
    console.log(`Setting variable ${name}...`);
    const createRes = await fetch(`${base}/api/v1/variables/`, {
      method: 'POST',
      headers: {
        ...headers,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        name,
        value,
        type,
        default_fields: [field]
      })
    });

    if (createRes.ok) {
      console.log(`  ${name} set successfully`);
    } else {
      const errText = await createRes.text();
      console.error(`  ${name} FAILED to set: ${errText}`);
    }
  }

  console.log("Setting flow's variables...");
  await setVariable('GEMINI_API_KEY', key, 'Credential', 'api_key');
  await setVariable('STEWARD_API_BASE_URL', stewardUrl, 'Generic', 'base_url');

  console.log('\nLangflow ready. Chat should answer once the backend is up.');
}

run();
