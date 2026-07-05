// Squarified-treemap geometry and the shared tone formatting, kept apart from the Treemap component so the
// component file exports only a component (fast-refresh friendly).

// Layout box for the treemap; tile positions are emitted as percentages of it, and the panel keeps this
// aspect ratio so the proportions hold at any width.
export const MAP_BOX_W = 100
export const MAP_BOX_H = 42
export const TONE_GLYPH = { up: '▲', down: '▼', flat: '–' }
export const TONE_WORD = { up: 'up', down: 'down', flat: 'unchanged' }

export function formatPct(value) {
  if (typeof value !== 'number') return '—'
  const sign = value > 0 ? '+' : value < 0 ? '−' : ''
  return `${sign}${(Math.abs(value) * 100).toFixed(1)}%`
}

export function heatMix(value) {
  if (typeof value !== 'number' || value === 0) return '0%'
  return `${Math.min(88, 44 + Math.abs(value) * 900).toFixed(0)}%`
}

export function mapTileSize(areaPct, widthPct, heightPct) {
  const shortestSide = Math.min(widthPct, heightPct)
  if (areaPct < 1.1 || shortestSide < 8) return 'is-tiny'
  if (areaPct < 2.2 || shortestSide < 13) return 'is-small'
  return ''
}

// Worst (largest) aspect ratio a row of tile areas would reach if laid along a side of the given length.
function worstRatio(areas, side, sum) {
  if (areas.length === 0 || sum <= 0) return Infinity
  const max = Math.max(...areas)
  const min = Math.min(...areas)
  const side2 = side * side
  const sum2 = sum * sum
  return Math.max((side2 * max) / sum2, sum2 / (side2 * min))
}

// Squarified treemap (Bruls, Huizing, van Wijk): packs items into the box with area proportional to
// value, growing each row only while it keeps tiles close to square. Items must be sorted largest first.
export function squarify(items, width, height) {
  const total = items.reduce((sum, item) => sum + item.value, 0)
  if (total <= 0) return []

  const scale = (width * height) / total
  const nodes = items.map((item) => ({ item, area: item.value * scale }))

  const placed = []
  let free = { x: 0, y: 0, w: width, h: height }
  let index = 0

  while (index < nodes.length) {
    const side = Math.min(free.w, free.h)
    const row = []
    let rowSum = 0

    while (index + row.length < nodes.length) {
      const next = nodes[index + row.length]
      const current = row.map((node) => node.area)
      const widened = [...current, next.area]
      if (row.length === 0 || worstRatio(widened, side, rowSum + next.area) <= worstRatio(current, side, rowSum)) {
        row.push(next)
        rowSum += next.area
      } else {
        break
      }
    }

    const thickness = rowSum / side
    if (free.w >= free.h) {
      let y = free.y
      for (const node of row) {
        const cellHeight = node.area / thickness
        placed.push({ ...node.item, x: free.x, y, w: thickness, h: cellHeight })
        y += cellHeight
      }
      free = { x: free.x + thickness, y: free.y, w: free.w - thickness, h: free.h }
    } else {
      let x = free.x
      for (const node of row) {
        const cellWidth = node.area / thickness
        placed.push({ ...node.item, x, y: free.y, w: cellWidth, h: thickness })
        x += cellWidth
      }
      free = { x: free.x, y: free.y + thickness, w: free.w, h: free.h - thickness }
    }

    index += row.length
  }

  return placed
}
