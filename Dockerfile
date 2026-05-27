FROM node:24-slim AS frontend-build

WORKDIR /app

COPY package.json ./
COPY frontend/package.json ./frontend/package.json
COPY frontend/package-lock.json ./frontend/package-lock.json
RUN npm ci --prefix frontend

COPY frontend ./frontend
RUN npm run build

FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY opencodex_proxy ./opencodex_proxy
COPY --from=frontend-build /app/opencodex_proxy/static/admin ./opencodex_proxy/static/admin
COPY .env.example ./.env.example

EXPOSE 8000

CMD ["python", "-m", "opencodex_proxy"]
