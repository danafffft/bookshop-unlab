# Постійне розміщення на Railway

Статус: підготовлено конфігурацію; акаунт хостингу ще не підключено, ресурси не створено і сайт не опубліковано.

Склад: публічний Go gateway, приватний ASP.NET Core API, PostgreSQL із постійним томом. Поточний Railway Hobby має мінімальну оплату $5/місяць; використання понад включені кредити оплачується окремо. Це не оцінка повної вартості трьох сервісів. Витрати необхідно погодити до запуску.

## Сервіси

Корінь вихідного репозиторію має містити api/, gateway/, web/, db/ та deploy/. Не включайте .env і секрети до Git.

1. Створити один Railway-проєкт та PostgreSQL через шаблон бази даних. Назва сервісу: Postgres. Використати приватне підключення, перевірити наявність persistent volume і ввімкнути резервні копії. Не відкривати публічний TCP proxy бази без потреби.
2. Створити API із цього вихідного коду. Файл конфігурації: /deploy/railway/api.json. Без публічного домену.
3. Створити gateway із того ж коду. Файл конфігурації: /deploy/railway/gateway.json. Згенерувати Railway HTTPS-домен із target port 8080.

## Змінні API

```
PORT=5080
ASPNETCORE_URLS=http://+:5080
DATABASE_URL=${{Postgres.DATABASE_URL}}
PUBLIC_ORIGIN=https://EXACT_GENERATED_GATEWAY_DOMAIN
COOKIE_SECURE=true
ADMIN_EMAIL=YOUR_ADMIN_EMAIL
ADMIN_PASSWORD=YOUR_UNIQUE_RANDOM_SECRET_AT_LEAST_12_CHARACTERS
```

Для DATABASE_URL вставити reference із приватної змінної PostgreSQL-сервісу, а не DATABASE_PUBLIC_URL. Задати PUBLIC_ORIGIN точним origin без кінцевого слеша. Для нового домену його потрібно оновити. Пароль адміністратора зберігати лише у секретних змінних хостингу. Наявного покупця автоматично не підвищуємо до адміністратора; використати окремий email.

## Змінні gateway

```
PORT=8080
API_URL=http://${{api.RAILWAY_PRIVATE_DOMAIN}}:5080
WEB_ROOT=/app/web
```

Назва api в reference має відповідати фактичній назві API-сервісу. Увімкнути постійну роботу сервісів: Serverless/App Sleeping вимкнено. ALWAYS у конфігураціях задає політику перезапуску процесів. Healthcheck /api/health перевіряє готовність API і бази перед увімкненням нової версії; він не є безперервним моніторингом.

## Перевірка перед передачею

- Успішна збірка та healthcheck обох сервісів.
- HTTPS відкриває каталог; обкладинки завантажуються; обидві мови й теми працюють.
- Реєстрація, cookie із Secure/HttpOnly, повторний вхід і вихід.
- Покупка створює запис у PostgreSQL; покупець бачить лише власні замовлення.
- Адмін змінює статус, редагує каталог; покупцю адміндоступ заборонено сервером.
- Після перезапуску API замовлення та користувачі збережені.
- Перевірені резервні копії, ресурси й бюджет. Конкретний доступний період резервних копій залежить від плану.

Офіційні джерела: https://docs.railway.com/guides/docker-compose , https://docs.railway.com/config-as-code/reference , https://docs.railway.com/databases/postgresql , https://docs.railway.com/deployments/healthchecks , https://docs.railway.com/pricing/plans .
