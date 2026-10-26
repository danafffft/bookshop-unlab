# Nova Poshta integration

The server calls the official https://api.novaposhta.ua/v2.0/json/ endpoint. Cities and branches are fetched with Address/getCities and Address/getWarehouses, cached for 15 minutes, and served to the browser without exposing an API key. These directory methods were verified without a key on 2026-10-26. If Nova Poshta requires a key for the account or changes access, add NOVA_POSHTA_API_KEY to Render's server environment.

Checkout stores the selected city and branch references with the order. The server checks the selected branch against the carrier's directory and uses the carrier's city and address, rather than trusting text sent by the browser.

Delivery cost is not invented and is not included in the book total. The recipient pays the applicable carrier tariff. Shipping labels are created manually by the seller after confirmation. Automated label creation requires sender account details, a valid account API key, parcel dimensions/weight and a separate shipping-label action; it is not silently executed when an order is placed.

Official provider information: https://novaposhta.ua/for-business/cooperation/integration/ . API key creation is available in the business account settings under Security.
