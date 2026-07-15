export function groupSettings(settings) {
  const sections = new Map()
  for (const setting of settings) {
    if (!sections.has(setting.section)) {
      sections.set(setting.section, new Map())
    }

    const subsections = sections.get(setting.section)
    const subsection = setting.subsection ?? null
    if (!subsections.has(subsection)) {
      subsections.set(subsection, [])
    }
    subsections.get(subsection).push(setting)
  }

  return Array.from(sections, ([section, subsections]) => ({
    section,
    subsections: Array.from(subsections, ([name, values]) => ({ name, settings: values }))
      .sort((left, right) => {
        if (left.name === null) return -1
        if (right.name === null) return 1
        return left.name.localeCompare(right.name)
      }),
  }))
}

export function toDraftValue(setting) {
  if (setting.valueType === 'StringList') {
    return setting.value.join('\n')
  }
  if (setting.valueType === 'Boolean') {
    return setting.value
  }
  return String(setting.value ?? '')
}

export function buildSettingsUpdate(settings, drafts, dirtyKeys) {
  const values = {}
  for (const setting of settings) {
    if (!dirtyKeys.has(setting.key)) continue

    const draft = drafts[setting.key]
    if (setting.valueType === 'Boolean') {
      values[setting.key] = Boolean(draft)
    } else if (setting.valueType === 'Integer' || setting.valueType === 'Decimal') {
      const number = Number(draft)
      values[setting.key] = Number.isFinite(number) && draft !== '' ? number : draft
    } else if (setting.valueType === 'StringList') {
      values[setting.key] = String(draft)
        .split('\n')
        .map((value) => value.trim())
        .filter(Boolean)
    } else {
      values[setting.key] = String(draft).trim()
    }
  }

  return { values }
}
