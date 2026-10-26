# Перевірка поточної збірки

У цьому середовищі успішно зібрано Go gateway і ASP.NET Core API (0 помилок і попереджень). `node --check web/app.js` та `node --check tests/integration.mjs` пройшли. Усі три тести Go успішні: захисні заголовки, заборона POST до статичних файлів, захист файлів поза коренем.

Локальний HTTP-доступ заборонений середовищем Windows: `An attempt was made to access a socket in a way forbidden by its access permissions`. Тому повна інтеграційна та візуальна перевірка не підтверджені. Обкладинки використовують зовнішній сервіс; їх завантаження тут також не вдалося перевірити через DNS.

Для інтеграційної перевірки підніміть окремий тестовий екземпляр із чистою PostgreSQL-базою. У PowerShell:

```
$env:TEST_ONLY='1'
$env:TEST_ORIGIN='http://localhost:8080'
$env:TEST_ADMIN_EMAIL='YOUR_TEST_ADMIN_EMAIL'
$env:TEST_ADMIN_PASSWORD='YOUR_TEST_ADMIN_PASSWORD'
node tests/integration.mjs
```

Сценарій створює тестових покупців і замовлення. З правами адміністратора він також перевіряє повернення залишків при скасуванні та паралельну купівлю останнього примірника. Не запускайте на робочій базі.

Перевірка Go: `cd gateway` та `go test -v .`.

