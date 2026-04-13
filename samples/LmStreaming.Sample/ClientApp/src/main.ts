import { createApp } from 'vue';
import App from './App.vue';
import './assets/styles.css';
import { logger } from './utils';

const log = logger.forComponent('App');

log.info('LmStreaming Chat Client starting', {
  userAgent: navigator.userAgent,
  url: window.location.href,
});

const app = createApp(App);
app.mount('#app');

log.info('Vue app mounted');
