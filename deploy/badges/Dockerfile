FROM python:3.12-alpine
WORKDIR /app
COPY tools/badges /app/tools/badges
COPY config /app/config
EXPOSE 8766
ENV KF_BADGES_PORT=8766
ENV KF_BADGES_HOST=0.0.0.0
ENV KF_BADGES_DB_PATH=/data/badges.sqlite3
CMD ["python", "tools/badges/server.py"]
