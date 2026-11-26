-- One database and one login per service. No service can read another service's data.
-- Local development passwords only.
CREATE USER accounts WITH PASSWORD 'accounts_dev';
CREATE DATABASE accounts_db OWNER accounts;

CREATE USER payments WITH PASSWORD 'payments_dev';
CREATE DATABASE payments_db OWNER payments;

CREATE USER risk WITH PASSWORD 'risk_dev';
CREATE DATABASE risk_db OWNER risk;

CREATE USER notifications WITH PASSWORD 'notifications_dev';
CREATE DATABASE notifications_db OWNER notifications;

CREATE USER banksim WITH PASSWORD 'banksim_dev';
CREATE DATABASE banksim_db OWNER banksim;

REVOKE CONNECT ON DATABASE accounts_db, payments_db, risk_db, notifications_db, banksim_db FROM PUBLIC;
