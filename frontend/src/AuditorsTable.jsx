import { formatInt } from './format'

// Roster table of rating agencies. Shared shape with the other roster tables: a name button that selects the
// row and an optional selectedId highlight.
export function AuditorsTable({ auditors, onSelectAuditor, selectedId }) {
  if (auditors.length === 0) {
    return <p className="note">No auditors yet. Seed or run the market to create them.</p>
  }

  return (
    <div className="tbl-scroll">
      <table className="tbl">
        <thead>
          <tr>
            <th scope="col">Auditor</th>
            <th scope="col">Focus</th>
            <th scope="col" className="ta-r">
              Audits
            </th>
          </tr>
        </thead>
        <tbody>
          {auditors.map((auditor) => {
            const isSelected = selectedId === auditor.id
            return (
              <tr
                key={auditor.id}
                className={isSelected ? 'is-selected' : undefined}
                aria-current={isSelected ? 'true' : undefined}
              >
                <th scope="row" className="cell-ellipsis">
                  <button type="button" className="cell-name-btn" onClick={() => onSelectAuditor?.(auditor.id)}>
                    {auditor.name}
                  </button>
                </th>
                <td className="cell-ellipsis">{auditor.description}</td>
                <td className="num ta-r">{formatInt(auditor.auditCount)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
