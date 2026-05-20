FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY opencodex_proxy ./opencodex_proxy
COPY .env.example ./.env.example

EXPOSE 8000

CMD ["python", "-m", "opencodex_proxy"]
