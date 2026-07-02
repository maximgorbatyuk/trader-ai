import { useEffect, useRef, useState } from 'react'

// Type-ahead picker for a single company: filters as you type, navigable by keyboard. Controlled by the
// parent through value (company id) and onChange.
export function CompanyCombobox({ companies, value, onChange }) {
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const [activeIndex, setActiveIndex] = useState(0)
  const rootRef = useRef(null)
  const listRef = useRef(null)

  const selected = companies.find((company) => String(company.id) === String(value)) ?? null
  const trimmed = query.trim().toLowerCase()
  const matches = trimmed
    ? companies.filter((company) => company.name.toLowerCase().includes(trimmed))
    : companies

  useEffect(() => {
    function onPointerDown(event) {
      if (rootRef.current && !rootRef.current.contains(event.target)) {
        setOpen(false)
        setQuery('')
      }
    }
    document.addEventListener('mousedown', onPointerDown)
    return () => document.removeEventListener('mousedown', onPointerDown)
  }, [])

  useEffect(() => {
    if (open) listRef.current?.querySelector('.is-active')?.scrollIntoView({ block: 'nearest' })
  }, [activeIndex, open])

  function openList() {
    setQuery('')
    setActiveIndex(0)
    setOpen(true)
  }

  function choose(company) {
    onChange(company.id)
    setOpen(false)
    setQuery('')
  }

  function onKeyDown(event) {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      if (!open) openList()
      else setActiveIndex((index) => Math.min(index + 1, matches.length - 1))
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setActiveIndex((index) => Math.max(index - 1, 0))
    } else if (event.key === 'Enter' && open) {
      event.preventDefault()
      if (matches[activeIndex]) choose(matches[activeIndex])
    } else if (event.key === 'Escape' && open) {
      // Close only the dropdown first; keep the keypress from reaching the modal's Escape-to-close.
      event.preventDefault()
      event.stopPropagation()
      setOpen(false)
      setQuery('')
    }
  }

  return (
    <div className="combobox" ref={rootRef}>
      <input
        className="select"
        type="text"
        role="combobox"
        aria-label="Company"
        aria-expanded={open}
        aria-controls="company-combobox-list"
        aria-autocomplete="list"
        autoComplete="off"
        placeholder="Search companies…"
        value={open ? query : selected?.name ?? ''}
        onChange={(event) => {
          setQuery(event.target.value)
          setActiveIndex(0)
          setOpen(true)
        }}
        onFocus={openList}
        onKeyDown={onKeyDown}
      />
      {open ? (
        <ul className="combobox-list" id="company-combobox-list" role="listbox" ref={listRef}>
          {matches.length === 0 ? (
            <li className="combobox-empty">No companies match.</li>
          ) : (
            matches.map((company, index) => (
              <li
                key={company.id}
                role="option"
                aria-selected={String(company.id) === String(value)}
                className={`combobox-option ${index === activeIndex ? 'is-active' : ''}`}
                onMouseEnter={() => setActiveIndex(index)}
                onMouseDown={(event) => {
                  event.preventDefault()
                  choose(company)
                }}
              >
                {company.name}
              </li>
            ))
          )}
        </ul>
      ) : null}
    </div>
  )
}
