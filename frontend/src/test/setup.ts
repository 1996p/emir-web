import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup, configure } from '@testing-library/react';

// Под coverage рендер медленнее — даём findBy* больше времени, чтобы не было ложных падений
configure({ asyncUtilTimeout: 5000 });

// Каждый тест начинается с чистого DOM
afterEach(() => cleanup());
