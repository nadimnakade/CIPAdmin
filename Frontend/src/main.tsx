import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
// Import order is deliberate. Page-scoped CSS (BsaAnalyzerPage.css) is pulled in
// transitively by App and lands FIRST; the global sheet follows, then the token
// layer LAST so its :root aliases are overridden by nothing and its canonical
// focus ring wins over styles.css's legacy :focus-visible rule.
// See docs/design-system/ROLLOUT.md, Phase 2.
import './styles.css';
// Component stylesheets load after the global sheet, so they win on equal
// specificity instead of needing inflated selectors. See docs/design-system/ROLLOUT.md.
import './components/Layout.css';
import './styles/tokens.css';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
