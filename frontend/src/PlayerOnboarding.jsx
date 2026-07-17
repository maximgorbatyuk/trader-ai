import { useState } from 'react'
import { api } from './api'
import { Modal } from './Modal'
import { PLAYER_ONBOARDING_STEPS } from './playerOnboardingModel'

export function PlayerCreationForm({ name, submitting, error, onNameChange, onSubmit, onBack }) {
  return (
    <form className="onboarding-form" onSubmit={onSubmit}>
      <label className="field">
        <span>Name</span>
        <input
          className="select"
          type="text"
          placeholder="Player"
          autoComplete="nickname"
          value={name}
          onChange={(event) => onNameChange(event.target.value)}
        />
      </label>
      {error ? (
        <p className="command-error" role="alert">
          {error}
        </p>
      ) : null}
      <div className="modal-foot onboarding-actions">
        <button type="button" className="btn" disabled={submitting} onClick={onBack}>
          Back
        </button>
        <button type="submit" className="btn btn-primary" disabled={submitting}>
          {submitting ? 'Creating…' : 'Create player'}
        </button>
      </div>
    </form>
  )
}

export function PlayerOnboarding({ onCreated }) {
  const [stepIndex, setStepIndex] = useState(0)
  const [name, setName] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState(null)
  const step = PLAYER_ONBOARDING_STEPS[stepIndex]
  const isFinalStep = stepIndex === PLAYER_ONBOARDING_STEPS.length - 1

  async function handleSubmit(event) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await api.createPlayer({ name: name.trim() || null })
      await onCreated()
    } catch (submitError) {
      setError(submitError.message)
      setSubmitting(false)
    }
  }

  return (
    <Modal titleId="player-onboarding-title" className="modal-onboarding" dismissible={false}>
      <header className="onboarding-head">
        <div>
          <p className="onboarding-progress-label" aria-live="polite">
            Step {stepIndex + 1} of {PLAYER_ONBOARDING_STEPS.length}
          </p>
          <h2 id="player-onboarding-title">{step.title}</h2>
        </div>
        <div
          className="onboarding-progress"
          role="progressbar"
          aria-label="Player introduction progress"
          aria-valuemin="1"
          aria-valuemax={PLAYER_ONBOARDING_STEPS.length}
          aria-valuenow={stepIndex + 1}
        >
          <span style={{ width: `${((stepIndex + 1) / PLAYER_ONBOARDING_STEPS.length) * 100}%` }} />
        </div>
      </header>

      <section className="onboarding-copy">
        <p>{step.description}</p>
        {step.points.length > 0 ? (
          <ul className="onboarding-list">
            {step.points.map((point) => (
              <li key={point}>{point}</li>
            ))}
          </ul>
        ) : null}
      </section>

      {isFinalStep ? (
        <PlayerCreationForm
          name={name}
          submitting={submitting}
          error={error}
          onNameChange={setName}
          onSubmit={handleSubmit}
          onBack={() => setStepIndex((current) => current - 1)}
        />
      ) : (
        <div className="modal-foot onboarding-actions">
          <button
            type="button"
            className="btn"
            disabled={stepIndex === 0}
            onClick={() => setStepIndex((current) => current - 1)}
          >
            Back
          </button>
          <button
            type="button"
            className="btn btn-primary"
            onClick={() => setStepIndex((current) => current + 1)}
          >
            Continue
          </button>
        </div>
      )}
    </Modal>
  )
}
