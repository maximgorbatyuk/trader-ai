import { formatCompactMoney, toneOf } from './format'
import { MAP_BOX_W, MAP_BOX_H, TONE_GLYPH, formatPct, heatMix, mapTileSize, squarify } from './treemapLayout'

// Squarified treemap of tiles sized by `value` and coloured by directional change. A caller may format that
// change as a percentage or another unit while the glyph keeps the direction legible without colour alone.
export function Treemap({ items, formatValue = formatCompactMoney, formatChange = formatPct, onSelect, ariaLabel }) {
  const sorted = [...items].sort((a, b) => b.value - a.value)
  const total = sorted.reduce((sum, item) => sum + item.value, 0)
  const tiles = squarify(
    sorted.map((item) => ({ item, value: item.value })),
    MAP_BOX_W,
    MAP_BOX_H,
  )

  return (
    <div
      className="market-map"
      style={{ aspectRatio: `${MAP_BOX_W} / ${MAP_BOX_H}` }}
      role={ariaLabel ? 'group' : undefined}
      aria-label={ariaLabel}
    >
      {tiles.map(({ item, x, y, w, h }) => {
        const tone = toneOf(item.changePct)
        const widthPct = (w / MAP_BOX_W) * 100
        const heightPct = (h / MAP_BOX_H) * 100
        const areaPct = total > 0 ? (item.value / total) * 100 : 0
        const sizeClass = mapTileSize(areaPct, widthPct, heightPct)
        return (
          <div
            key={item.id}
            className={`map-tile tone-bg-${tone} ${sizeClass}`}
            role="button"
            tabIndex={0}
            onClick={() => onSelect?.(item.id)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault()
                onSelect?.(item.id)
              }
            }}
            style={{
              left: `${(x / MAP_BOX_W) * 100}%`,
              top: `${(y / MAP_BOX_H) * 100}%`,
              width: `${widthPct}%`,
              height: `${heightPct}%`,
              '--map-area': areaPct.toFixed(2),
              '--map-heat': heatMix(item.changePct),
            }}
            title={item.title}
            aria-label={item.ariaLabel}
          >
            {item.halted ? <span className="map-halt">{item.halted}</span> : null}
            <span className="map-name">{item.label}</span>
            <span className="map-cap num">{formatValue(item.value)}</span>
            <span className="map-change num">
              <span aria-hidden="true">{TONE_GLYPH[tone]}</span> {formatChange(item.changePct)}
            </span>
          </div>
        )
      })}
    </div>
  )
}
