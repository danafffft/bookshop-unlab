// Run ONLY against a dedicated test database: TEST_ONLY=1 node tests/integration.mjs.
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
if(process.env.TEST_ONLY!=='1')throw new Error('Set TEST_ONLY=1 and use a disposable test database. This script creates accounts and orders.');
const base=process.env.TEST_ORIGIN||'http://localhost:8080';
function client(){let cookie='';return async(path,method='GET',body,expected=200,origin=base)=>{const r=await fetch(base+'/api'+path,{method,headers:{...(cookie?{Cookie:cookie}:{}),...(body?{'Content-Type':'application/json'}:{}),Origin:origin},body:body?JSON.stringify(body):undefined});const text=await r.text();assert.equal(r.status,expected,`${method} ${path}: ${text}`);const set=r.headers.get('set-cookie');if(set)cookie=set.split(';')[0];return text?JSON.parse(text):null}}
const alice=client(),bob=client(),anonymous=client();
const password='Test-'+randomUUID();
const initial=await anonymous('/books');assert(initial.length>=8);const book=initial.find(b=>b.stock>=2);assert(book);
await anonymous('/orders','GET',undefined,401);
await anonymous('/admin/books','GET',undefined,401);
await alice('/auth/register','POST',{email:randomUUID()+'@example.test',password,name:'Test Alice'});
await bob('/auth/register','POST',{email:randomUUID()+'@example.test',password,name:'Test Bob'});
await alice('/admin/books','GET',undefined,403);
await alice('/auth/logout','POST',undefined,403,'https://attacker.example');
const order=await alice('/orders','POST',{name:'Test Alice',phone:'+380000000000',city:'Test city',address:'Test branch 1',items:[{bookId:book.id,quantity:2}],total:1});
assert.equal(order.total,book.price*2,'server must ignore browser total');
const after=await anonymous('/books');assert.equal(after.find(b=>b.id===book.id).stock,book.stock-2);
assert((await alice('/orders')).some(o=>o.id===order.id));
assert(!(await bob('/orders')).some(o=>o.id===order.id),'order must be private');
await alice('/orders','POST',{name:'Test Alice',phone:'+380000000000',city:'Test city',address:'Test branch 1',items:[{bookId:book.id,quantity:21}]},400);
if(process.env.TEST_ADMIN_EMAIL&&process.env.TEST_ADMIN_PASSWORD){
 const admin=client();await admin('/auth/login','POST',{email:process.env.TEST_ADMIN_EMAIL,password:process.env.TEST_ADMIN_PASSWORD});
 await admin('/admin/orders/'+order.id,'PATCH',{status:'cancelled'});
 await admin('/admin/orders/'+order.id,'PATCH',{status:'cancelled'},409);
 assert.equal((await anonymous('/books')).find(b=>b.id===book.id).stock,book.stock,'cancellation restores exactly once');
 const original=(await admin('/admin/books')).find(b=>b.id===book.id);
 await admin('/admin/books/'+book.id,'PUT',{...original,stock:1});
 const payload={name:'Test Person',phone:'+380000000000',city:'Test city',address:'Test branch 1',items:[{bookId:book.id,quantity:1}]};
 const attempt=async(c)=>{try{return {ok:true,value:await c('/orders','POST',payload)}}catch{return {ok:false}}};
 const concurrent=await Promise.all([attempt(alice),attempt(bob)]);
 assert.equal(concurrent.filter(r=>r.ok).length,1,'last copy must only sell once');
 for(const r of concurrent)if(r.ok)await admin('/admin/orders/'+r.value.id,'PATCH',{status:'cancelled'});
 await admin('/admin/books/'+book.id,'PUT',original);
}
await alice('/auth/logout','POST');await alice('/orders','GET',undefined,401);
console.log('PASS: registration, sessions, CSRF, permissions, order privacy, authoritative pricing, stock transaction, logout. Admin cancellation and last-copy concurrency run when admin credentials are supplied.');
