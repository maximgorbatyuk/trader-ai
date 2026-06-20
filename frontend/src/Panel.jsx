// Shared card shell: a titled panel with an optional count chip and extra header content.
export function Panel({ title, count, className = '', headerExtra, children }) {
  return (
    <article className={`panel ${className}`}>
      <div className="panel-head">
        <h2>{title}</h2>
        <div className="panel-head-meta">
          {typeof count === 'string' ? <span className="panel-count">{count}</span> : null}
          {headerExtra}
        </div>
      </div>
      {children}
    </article>
  )
}
