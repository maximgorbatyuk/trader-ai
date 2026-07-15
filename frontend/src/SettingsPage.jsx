import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { buildSettingsUpdate, groupSettings, toDraftValue } from './settingsModel'

export function SettingsPage() {
  const [settings, setSettings] = useState(null)
  const [drafts, setDrafts] = useState({})
  const [dirtyKeys, setDirtyKeys] = useState(() => new Set())
  const [loadError, setLoadError] = useState(null)
  const [saveError, setSaveError] = useState(null)
  const [fieldErrors, setFieldErrors] = useState({})
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)

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
