// Row of small preset buttons that fill an order field from a base value — quantity as a percentage of the
// max, or price as a delta from the current price. Each option carries its own value; the caller decides
// what to do with it in onPick.
export function PercentButtons({ options, onPick, ariaLabel }) {
  return (
    <div className="pct-buttons" role="group" aria-label={ariaLabel}>
      {options.map((option) => (
        <button key={option.label} type="button" className="btn pct-btn" onClick={() => onPick(option.value)}>
          {option.label}
        </button>
      ))}
    </div>
  )
}
