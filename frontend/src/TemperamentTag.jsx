import { TEMPERAMENT_TAG_CLASS } from './format'

// Small colored label for a trader's temperament, shown beside the name in the roster table and the summary
// modal. Personality drives no logic for the human-controlled Player, so it renders nothing for that type.
export function TemperamentTag({ temperament, type }) {
  if (!temperament || type === 'Player') {
    return null
  }

  const modifier = TEMPERAMENT_TAG_CLASS[temperament] ?? ''
  return <span className={`tag ${modifier}`}>{temperament}</span>
}
