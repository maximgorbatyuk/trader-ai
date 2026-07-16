import { useCallback, useEffect, useMemo, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { api } from './api'
import { buildSettingsUpdate, groupSettings, toDraftValue } from './settingsModel'

const REPOSITORY_URL = 'https://github.com/maximgorbatyuk/trader-ai'
const SETTINGS_LINK_GROUPS = [
  {
    ariaLabel: 'Project links',
    title: 'Project',
    links: [
      { label: 'Concept', href: `${REPOSITORY_URL}/blob/main/docs/domain.md` },
      { label: 'About', href: `${REPOSITORY_URL}#trader-ai` },
    ],
  },
  {
    ariaLabel: 'Repository links',
    title: 'Repository',
    links: [
      { label: 'Github', href: REPOSITORY_URL },
      { label: 'Issues', href: `${REPOSITORY_URL}/issues` },
    ],
  },
  {
    ariaLabel: 'AI provider usage',
    title: 'AI provider usage',
    links: [
      { label: 'MiniMax', href: 'https://platform.minimax.io/console/usage' },
      { label: 'GLM', href: 'https://z.ai/manage-apikey/coding-plan/personal/usage' },
      { label: 'OpenAI', href: 'https://platform.openai.com/usage' },
      { label: 'Claude', href: 'https://platform.claude.com/usage' },
    ],
  },
]

export function SettingsPage() {
  const { runAction, resetMarket, pending } = useOutletContext() ?? {}
  const [settings, setSettings] = useState(null)
  const [drafts, setDrafts] = useState({})
  const [dirtyKeys, setDirtyKeys] = useState(() => new Set())
  const [loadError, setLoadError] = useState(null)
  const [saveError, setSaveError] = useState(null)
  const [fieldErrors, setFieldErrors] = useState({})
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [confirmingReset, setConfirmingReset] = useState(false)

  useEffect(() => {
    if (!confirmingReset) return undefined

    const timer = setTimeout(() => setConfirmingReset(false), 5000)
    return () => clearTimeout(timer)
  }, [confirmingReset])

  function handleResetMarket() {
    if (!confirmingReset) {
      setConfirmingReset(true)
      return
    }

    setConfirmingReset(false)
    runAction(resetMarket)
  }

  const load = useCallback(async () => {
    setLoadError(null)
    try {
      const values = await api.getSettings()
      setSettings(values)
      setDrafts(Object.fromEntries(values.map((setting) => [setting.key, toDraftValue(setting)])))
      setDirtyKeys(new Set())
    } catch (error) {
      setLoadError(error.message)
    }
  }, [])

  useEffect(() => {
    const initialId = setTimeout(load, 0)
    return () => clearTimeout(initialId)
  }, [load])

  const groups = useMemo(() => groupSettings(settings ?? []), [settings])

  function updateDraft(key, value) {
    setDrafts((current) => ({ ...current, [key]: value }))
    setDirtyKeys((current) => new Set(current).add(key))
    setFieldErrors((current) => {
      if (!current[key]) return current
      const next = { ...current }
      delete next[key]
      return next
    })
    setSaved(false)
  }

  async function save() {
    setSaving(true)
    setSaveError(null)
    setFieldErrors({})
    setSaved(false)
    try {
      const updated = await api.updateSettings(buildSettingsUpdate(settings, drafts, dirtyKeys))
      setSettings(updated)
      setDrafts(Object.fromEntries(updated.map((setting) => [setting.key, toDraftValue(setting)])))
      setDirtyKeys(new Set())
      setSaved(true)
    } catch (error) {
      setSaveError(error.message)
      setFieldErrors(error.fieldErrors ?? {})
    } finally {
      setSaving(false)
    }
  }

  return (
    <main className="main settings-page">
      <header className="settings-header">
        <div>
          <h1>Settings</h1>
          <p>Game-process and AI-provider configuration. Saved values apply to the next market operation.</p>
        </div>
        <button
          type="button"
          className="btn btn-primary"
          disabled={saving || dirtyKeys.size === 0}
          onClick={save}
        >
          {saving ? 'Saving…' : dirtyKeys.size > 0 ? `Save changes (${dirtyKeys.size})` : 'Save changes'}
        </button>
      </header>

      {settings === null && !loadError ? (
        <section className="placeholder settings-loading" role="status" aria-busy="true">
          <span className="spinner" aria-hidden="true" />
          <p>Loading settings…</p>
        </section>
      ) : null}

      {loadError ? (
        <div className="banner" role="alert">
          <strong>Settings could not be loaded.</strong>
          <span>{loadError}</span>
          <button type="button" className="btn" onClick={load}>Retry</button>
        </div>
      ) : null}

      {saveError ? (
        <div className="banner" role="alert">
          <strong>Settings were not saved.</strong>
          <span>{saveError}</span>
          <ValidationSummary fieldErrors={fieldErrors} />
        </div>
      ) : null}

      {saved ? <p className="settings-save-status" role="status">Settings saved.</p> : null}

      {settings?.length === 0 ? (
        <section className="placeholder">
          <p>No game settings are available.</p>
        </section>
      ) : null}

      {groups.map((group) => (
        <SettingsGroup
          key={group.section}
          group={group}
          drafts={drafts}
          dirtyKeys={dirtyKeys}
          fieldErrors={fieldErrors}
          disabled={saving}
          onChange={updateDraft}
        />
      ))}

      <SettingsLinks />

      <section className="settings-section" aria-labelledby="settings-database">
        <header className="settings-section-header">
          <h2 id="settings-database">Database</h2>
        </header>
        <div className="settings-subsection">
          <p className="settings-description">
            Erase the current market and reseed the demo database. This cannot be undone.
          </p>
          <button
            type="button"
            className={`btn btn-reset${confirmingReset ? ' btn-reset-armed' : ''}`}
            disabled={pending}
            title={confirmingReset ? 'Click again to erase and reseed the demo database' : 'Erase and reseed the demo database'}
            onClick={handleResetMarket}
          >
            {confirmingReset ? 'Confirm reset' : 'Reset DB'}
          </button>
        </div>
      </section>
    </main>
  )
}

export function ValidationSummary({ fieldErrors }) {
  const entries = Object.entries(fieldErrors)
  if (entries.length === 0) return null

  return (
    <ul className="settings-validation-list">
      {entries.flatMap(([key, messages]) => messages.map((message, index) => (
        <li key={`${key}-${index}`}>
          <code>{key}</code>
          <span>{message}</span>
        </li>
      )))}
    </ul>
  )
}

// Project, repository, and AI-provider usage links, moved here from the former global footer so they sit
// beside the AI-provider settings.
export function SettingsLinks() {
  return (
    <section className="settings-section" aria-labelledby="settings-links">
      <header className="settings-section-header">
        <h2 id="settings-links">Links</h2>
      </header>
      <div className="settings-links">
        {SETTINGS_LINK_GROUPS.map(({ ariaLabel, title, links }) => (
          <nav className="settings-links-group" aria-label={ariaLabel} key={ariaLabel}>
            <h3>{title}</h3>
            <ul>
              {links.map((link) => (
                <li key={link.label}>
                  <a href={link.href} target="_blank" rel="noreferrer">
                    {link.label}
                  </a>
                </li>
              ))}
            </ul>
          </nav>
        ))}
      </div>
    </section>
  )
}

function SettingsGroup({ group, drafts, dirtyKeys, fieldErrors, disabled, onChange }) {
  const sectionId = `settings-${group.section.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
  const count = group.subsections.reduce((total, subsection) => total + subsection.settings.length, 0)
  return (
    <section className="settings-section" aria-labelledby={sectionId}>
      <header className="settings-section-header">
        <h2 id={sectionId}>{group.section}</h2>
        <span className="settings-count num">{count}</span>
      </header>
      {group.subsections.map((subsection) => (
        <div className="settings-subsection" key={subsection.name ?? 'general'}>
          {subsection.name ? <h3>{subsection.name}</h3> : null}
          <div className="settings-table-wrap">
            <table className="tbl settings-table">
              <thead>
                <tr>
                  <th scope="col">Setting</th>
                  <th scope="col">Description</th>
                  <th scope="col">Value</th>
                </tr>
              </thead>
              <tbody>
                {subsection.settings.map((setting) => (
                  <SettingsRow
                    key={setting.key}
                    setting={setting}
                    value={drafts[setting.key]}
                    dirty={dirtyKeys.has(setting.key)}
                    errors={fieldErrors[setting.key]}
                    disabled={disabled}
                    onChange={(value) => onChange(setting.key, value)}
                  />
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ))}
    </section>
  )
}

function SettingsRow({ setting, value, dirty, errors, disabled, onChange }) {
  const inputId = `setting-${setting.key.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
  const errorId = `${inputId}-error`
  return (
    <tr className={dirty ? 'is-dirty' : undefined}>
      <th scope="row">
        <label htmlFor={inputId}>{setting.name}</label>
        <code className="settings-key">{setting.key}</code>
      </th>
      <td className="settings-description">{setting.description}</td>
      <td className="settings-value">
        <SettingInput
          setting={setting}
          id={inputId}
          value={value}
          disabled={disabled}
          invalid={Boolean(errors?.length)}
          errorId={errors?.length ? errorId : undefined}
          onChange={onChange}
        />
        {errors?.length ? <span className="settings-field-error" id={errorId}>{errors.join(' ')}</span> : null}
      </td>
    </tr>
  )
}

function SettingInput({ setting, id, value, disabled, invalid, errorId, onChange }) {
  const common = {
    id,
    disabled,
    'aria-invalid': invalid || undefined,
    'aria-describedby': errorId,
  }
  if (setting.valueType === 'Boolean') {
    return (
      <input
        {...common}
        className="settings-checkbox"
        type="checkbox"
        checked={Boolean(value)}
        onChange={(event) => onChange(event.target.checked)}
      />
    )
  }
  if (setting.valueType === 'StringList') {
    return (
      <textarea
        {...common}
        className="select settings-input settings-list-input"
        rows="3"
        value={value ?? ''}
        onChange={(event) => onChange(event.target.value)}
      />
    )
  }
  if (setting.valueType === 'MultilineText') {
    return (
      <textarea
        {...common}
        className="select settings-input settings-prompt-input"
        rows="16"
        value={value ?? ''}
        onChange={(event) => onChange(event.target.value)}
      />
    )
  }
  return (
    <input
      {...common}
      className={`select settings-input${setting.valueType === 'Integer' || setting.valueType === 'Decimal' ? ' num' : ''}`}
      type={setting.valueType === 'Integer' || setting.valueType === 'Decimal' ? 'number' : setting.valueType === 'Url' ? 'url' : 'text'}
      step={setting.valueType === 'Integer' ? '1' : setting.valueType === 'Decimal' ? 'any' : undefined}
      value={value ?? ''}
      onChange={(event) => onChange(event.target.value)}
    />
  )
}
