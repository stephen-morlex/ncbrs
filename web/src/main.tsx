import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { AuthProvider } from 'react-oidc-context'
import './index.css'
import App from './App.tsx'
import { oidcConfig, readKeycloakSettings } from '@/auth/oidc'

// Read once, at startup, so a missing VITE_KEYCLOAK_* fails here with a
// message naming the file to copy -- rather than at the moment a registrar
// presses sign in and gets an opaque redirect failure.
const settings = readKeycloakSettings()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthProvider {...oidcConfig(settings)}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </AuthProvider>
  </StrictMode>,
)
