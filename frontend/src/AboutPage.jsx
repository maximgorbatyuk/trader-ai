import { useEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { ABOUT_DOCUMENTS, aboutDocumentKeyForHref, aboutTabKeyAfterKeyDown } from './aboutPageModel'
import domainSource from '../../docs/domain.md?raw'
import participantRulesSource from '../../docs/participant-rules.md?raw'
import aiAgentSource from '../../docs/roles/ai-agent.md?raw'
import auditorsSource from '../../docs/roles/auditors.md?raw'
import collectiveFundSource from '../../docs/roles/collective-fund.md?raw'
import companySource from '../../docs/roles/company.md?raw'
import fundMemberSource from '../../docs/roles/fund-member.md?raw'
import individualSource from '../../docs/roles/individual.md?raw'
import playerSource from '../../docs/roles/player.md?raw'
import luldSource from '../../docs/rules/luld.md?raw'
import sharePriceFormationSource from '../../docs/rules/share-price-formation.md?raw'
import tradingDaysSource from '../../docs/rules/trading-days.md?raw'
import bankLoansSource from '../../docs/logic/bank-loans.md?raw'
import behavioralAuditSource from '../../docs/logic/behavioral-audit.md?raw'
import bigInvestmentSource from '../../docs/logic/big-investment.md?raw'
import corporateCashSource from '../../docs/logic/corporate-cash.md?raw'
import crisisSource from '../../docs/logic/crisis.md?raw'
import freeShareEmissionSource from '../../docs/logic/free-share-emission.md?raw'
import fundAdvertisingSource from '../../docs/logic/fund-advertising.md?raw'
import marginSource from '../../docs/logic/margin.md?raw'
import sectorSentimentSource from '../../docs/logic/sector-sentiment.md?raw'
import settlementSource from '../../docs/logic/settlement.md?raw'

const DOCUMENT_SOURCE_BY_KEY = {
  domain: domainSource,
  'participant-rules': participantRulesSource,
  individual: individualSource,
  'ai-agent': aiAgentSource,
  player: playerSource,
  company: companySource,
  'collective-fund': collectiveFundSource,
  'fund-member': fundMemberSource,
  auditors: auditorsSource,
  'share-price-formation': sharePriceFormationSource,
  'trading-days': tradingDaysSource,
  luld: luldSource,
  settlement: settlementSource,
  margin: marginSource,
  crisis: crisisSource,
  'corporate-cash': corporateCashSource,
  'sector-sentiment': sectorSentimentSource,
  'free-share-emission': freeShareEmissionSource,
  'big-investment': bigInvestmentSource,
  'bank-loans': bankLoansSource,
  'fund-advertising': fundAdvertisingSource,
  'behavioral-audit': behavioralAuditSource,
}

export function AboutPage() {
  const [activeKey, setActiveKey] = useState(ABOUT_DOCUMENTS[0].key)
  const tabRefs = useRef({})
  const documentRef = useRef(null)
  const activeDocument = ABOUT_DOCUMENTS.find((document) => document.key === activeKey) ?? ABOUT_DOCUMENTS[0]

  useEffect(() => {
    documentRef.current?.scrollTo({ top: 0 })
  }, [activeKey])

  function focusTab(key) {
    setActiveKey(key)
    tabRefs.current[key]?.focus()
  }

  function onTabKeyDown(event) {
    const targetKey = aboutTabKeyAfterKeyDown(activeKey, event.key)
    if (!targetKey) return
    event.preventDefault()
    focusTab(targetKey)
  }

  const markdownComponents = {
    h1({ children }) {
      return <h2>{children}</h2>
    },
    h2({ children }) {
      return <h3>{children}</h3>
    },
    h3({ children }) {
      return <h4>{children}</h4>
    },
    h4({ children }) {
      return <h5>{children}</h5>
    },
    table({ children }) {
      return (
        <div className="about-table-wrap">
          <table>{children}</table>
        </div>
      )
    },
    a({ href, children }) {
      const targetKey = aboutDocumentKeyForHref(href, activeDocument.sourcePath)
      if (targetKey) {
        return (
          <button type="button" className="about-doc-link" onClick={() => focusTab(targetKey)}>
            {children}
          </button>
        )
      }
      return <a href={href}>{children}</a>
    },
  }

  return (
    <main className="main about-page">
      <header className="about-header">
        <h1>About</h1>
        <p>Domain guide for the market simulation, its participants, trading rules, and supporting mechanics.</p>
      </header>

      <article className="panel about-help">
        <div className="tabbar about-tabbar">
          <div className="tabs about-tabs" role="tablist" aria-label="About documentation" onKeyDown={onTabKeyDown}>
            {ABOUT_DOCUMENTS.map((document) => {
              const selected = document.key === activeDocument.key
              return (
                <button
                  key={document.key}
                  type="button"
                  role="tab"
                  id={`about-tab-${document.key}`}
                  aria-selected={selected}
                  aria-controls={selected ? `about-panel-${document.key}` : undefined}
                  tabIndex={selected ? 0 : -1}
                  ref={(element) => {
                    tabRefs.current[document.key] = element
                  }}
                  className={`tab${selected ? ' is-active' : ''}`}
                  onClick={() => setActiveKey(document.key)}
                >
                  {document.label}
                </button>
              )
            })}
          </div>
        </div>

        <section
          ref={documentRef}
          className="tabpanel about-document"
          role="tabpanel"
          id={`about-panel-${activeDocument.key}`}
          aria-labelledby={`about-tab-${activeDocument.key}`}
        >
          <ReactMarkdown remarkPlugins={[remarkGfm]} components={markdownComponents}>
            {DOCUMENT_SOURCE_BY_KEY[activeDocument.key]}
          </ReactMarkdown>
        </section>
      </article>
    </main>
  )
}
