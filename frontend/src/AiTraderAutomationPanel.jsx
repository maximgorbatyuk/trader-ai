import { useEffect, useMemo, useState } from 'react'
import './aiTrader.css'
import { api } from './api'
import { Panel } from './Panel'
import { AiRawResponseModal } from './AiRawResponseModal'
import { automationPayload, testRequestPayload, validateAutomation } from './aiTraderModel'

const AUTOMATION_TYPES = [
  { value: 'Individual', label: 'Individual (rule-based)' },
  { value: 'AIAgent', label: 'AI agent' },
]

// Detail-page control for converting a trader between rule-based Individual and a provider-backed AI agent. The
// key input is write-only and never seeded from server data. The form keeps its own local edit state and the
// parent remounts it per participant (via key), so the detail page's polling never clobbers an in-progress edit.
export function AiTraderAutomationPanel({ participantId, detail, onChanged }) {
  const [providers, setProviders] = useState([])
  const [type, setType] = useState(detail.type === 'AIAgent' ? 'AIAgent' : 'Individual')
  const [providerId, setProviderId] = useState(detail.aiProviderId ?? '')
  const [model, setModel] = useState(detail.aiModel ?? '')
  const [apiKey, setApiKey] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState(null)
  const [test, setTest] = useState({ status: 'idle', message: '', responseBody: null, statusCode: null })
  const [showRawResponse, setShowRawResponse] = useState(false)

  const originalProviderId = detail.aiProviderId ?? null

  useEffect(() => {
    let active = true
    api
      .getAiProviders()
      .then((list) => {
        if (!active) return
        const catalog = list ?? []
        setProviders(catalog)
        if (type === 'AIAgent' && !providerId && catalog.length > 0) {
          setProviderId(catalog[0].id)
        }
      })
      .catch(() => {})
    return () => {
      active = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const selectedProvider = useMemo(
    () => providers.find((provider) => provider.id === providerId),
    [providers, providerId],
  )
  const models = selectedProvider?.models ?? []

  const formState = { type, providerId, model, apiKey, originalProviderId }
  const validation = validateAutomation(formState)
  const runtimeStatus = detail.type === 'AIAgent' ? detail.aiStatus : null

  function handleTypeChange(nextType) {
    setType(nextType)
    if (nextType === 'AIAgent' && !providerId && providers.length > 0) {
      setProviderId(providers[0].id)
    }
  }

  async function handleSave() {
    setSaving(true)
    setError(null)
    try {
      await api.updateParticipantAutomation(participantId, automationPayload(formState))
      setApiKey('')
      onChanged?.()
    } catch (saveError) {
      setError(saveError.message)
    } finally {
      setSaving(false)
    }
  }

  async function handleTest() {
    setShowRawResponse(false)
    setTest({ status: 'testing', message: '', responseBody: null, statusCode: null })
    try {
      const result = await api.testParticipantAutomation(participantId, testRequestPayload(formState))
      setTest({
        status: result?.success ? 'ok' : 'error',
        message: result?.success
          ? result.assistantContent ?? '(empty reply)'
          : result?.error ?? 'The test failed.',
        responseBody: result?.responseBody ?? null,
        statusCode: result?.httpStatusCode ?? null,
      })
    } catch (testError) {
      setTest({ status: 'error', message: testError.message, responseBody: null, statusCode: null })
    }
  }

  return (
    <Panel
      title="Automation"
      count={detail.type === 'AIAgent' ? 'AI agent' : 'Rule-based'}
      className="panel-automation"
    >
      <div className="profile-form">
        <label className="field">
          <span>Trader type</span>
          <select className="select" value={type} onChange={(event) => handleTypeChange(event.target.value)}>
            {AUTOMATION_TYPES.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>

        {type === 'AIAgent' ? (
          <>
            <label className="field">
              <span>Provider</span>
              <select className="select" value={providerId} onChange={(event) => setProviderId(event.target.value)}>
                <option value="" disabled>
                  Select a provider
                </option>
                {providers.map((provider) => (
                  <option key={provider.id} value={provider.id}>
                    {provider.label}
                  </option>
                ))}
              </select>
            </label>

            <label className="field">
              <span>Model</span>
              <input
                className="select"
                type="text"
                autoComplete="off"
                spellCheck="false"
                list={models.length > 0 ? 'ai-model-suggestions' : undefined}
                value={model}
                onChange={(event) => setModel(event.target.value)}
                placeholder="Type the provider model name"
              />
              {models.length > 0 ? (
                <datalist id="ai-model-suggestions">
                  {models.map((option) => (
                    <option key={option} value={option} />
                  ))}
                </datalist>
              ) : null}
            </label>

            <label className="field">
              <span>API key</span>
              <input
                className="select"
                type="password"
                autoComplete="off"
                value={apiKey}
                onChange={(event) => setApiKey(event.target.value)}
                placeholder={detail.hasAiApiKey ? 'Enter a new key only to replace it' : 'Enter the provider API key'}
              />
            </label>
            {detail.hasAiApiKey ? (
              <p className="note">API key configured. Enter a new key only to replace it.</p>
            ) : null}

            <button
              className="btn"
              type="button"
              onClick={handleTest}
              disabled={test.status === 'testing' || !providerId || !model}
            >
              {test.status === 'testing' ? 'Testing…' : 'Test model ("Who are you")'}
            </button>
            {test.status === 'ok' ? (
              <p className="ai-test-reply" role="status">
                Reply: {test.message}
              </p>
            ) : null}
            {test.status === 'error' ? (
              <p className="command-error" role="alert">
                Test failed: {test.message}
              </p>
            ) : null}
            {(test.status === 'ok' || test.status === 'error') && test.responseBody ? (
              <button className="btn" type="button" onClick={() => setShowRawResponse(true)}>
                View raw response
              </button>
            ) : null}

            {runtimeStatus ? (
              <p className="ai-status-line">
                Status: <span className="tag">{runtimeStatus}</span>
                {detail.aiStatusMessage ? <span className="muted-sub"> — {detail.aiStatusMessage}</span> : null}
              </p>
            ) : null}
          </>
        ) : null}

        <button
          className="btn btn-primary btn-block"
          type="button"
          onClick={handleSave}
          disabled={saving || !validation.valid}
        >
          {saving ? 'Saving…' : 'Save automation'}
        </button>
        {!validation.valid ? <p className="note">{validation.error}</p> : null}
        {error ? (
          <p className="command-error" role="alert">
            {error}
          </p>
        ) : null}
      </div>
      {showRawResponse ? (
        <AiRawResponseModal
          statusCode={test.statusCode}
          body={test.responseBody}
          onClose={() => setShowRawResponse(false)}
        />
      ) : null}
    </Panel>
  )
}
