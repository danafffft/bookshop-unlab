# Увімкнення входу через Google

Код уже підготовлений у ASP.NET Core з OAuth, PKCE та захисними cookie. Реєстрація через email продовжує працювати. Кнопка Google активується тільки після додавання вашого OAuth-клієнта — сайт не імітує авторизацію.

1. Увійдіть у https://console.cloud.google.com/ та створіть або оберіть власний проєкт.
2. У Google Auth Platform налаштуйте Branding і Audience. Контактна пошта: miliciabgdana@gmail.com. Для входу достатньо openid, email і profile.
3. У Clients створіть OAuth client типу Web application.
4. Authorized JavaScript origin: https://cherry-books.onrender.com
5. Authorized redirect URI (точно): https://cherry-books.onrender.com/api/auth/google/callback
6. На Render відкрийте Environment вашого сервісу й додайте GOOGLE_CLIENT_ID та GOOGLE_CLIENT_SECRET. Секрет не надсилайте в чат і не додавайте до GitHub. Збережіть зі створенням нового deploy.
7. Якщо Google-застосунок має статус Testing, додайте свій Google-акаунт до Test users. Для інших покупців потрібно виконати умови публікації Google для вашого застосунку.

Після перезапуску /api/auth/providers поверне google:true, і кнопка стане активною. Перевірте вхід, повернення на /account/, вихід і повторне відкриття кабінету. Фінальний вхід і підтвердження доступу до Google-акаунта виконує його власник.

Акаунти ідентифікуються за унікальним Google subject. Google-вхід не приєднується автоматично до наявного облікового запису з паролем і такою самою поштою, щоб уникнути захоплення акаунтів. Для такого запису потрібно скористатися своїм попереднім паролем.

Офіційна документація: https://developers.google.com/identity/protocols/oauth2/web-server та https://learn.microsoft.com/aspnet/core/security/authentication/social/google-logins .
