import { createApp } from 'vue';
import App from './App.vue';
import './assets/styles.css';
// highlight.js token colours first, so markdown.css can override the theme's own
// block background/padding and keep code blocks inside our panel treatment.
import 'highlight.js/styles/github.css';
// KaTeX ships its own fonts. Importing the stylesheet from the package (never a CDN) makes Vite
// resolve the `url(fonts/KaTeX_*.woff2)` references and emit them into the build output.
import 'katex/dist/katex.min.css';
import './assets/markdown.css';
import { logger } from './utils';

const log = logger.forComponent('App');

log.info('LmStreaming Chat Client starting', {
  userAgent: navigator.userAgent,
  url: window.location.href,
});

const app = createApp(App);
app.mount('#app');

log.info('Vue app mounted');
