FROM cypress/base:10

WORKDIR /app/frontend

COPY ./frontend/package.json ./frontend/package-lock.json ./frontend/tsconfig.json ./
RUN npm ci

WORKDIR /app
COPY . .

ENV XDG_CONFIG_HOME /app
WORKDIR /app/frontend

ENTRYPOINT npm run cyrun -- \
# It seems that Cypress can't resolve the API_URL in time to intercept it which
# is why we use localhost:5000 instead of backend:5000
--env FRONTEND_URL=http://frontend:3000,API_URL=http://localhost:5000,AUTH_URL=http://mock-auth:8080 \
--config-file ./cypress.json \
# To run locally the following line should be commented out
--record --key ${CYPRESS_RECORD_KEY}
