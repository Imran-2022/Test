-- Secure Dynamic Search and Pagination


CREATE TABLE Customers(Id INT PRIMARY KEY, Name VARCHAR(100), Country CHAR(2), City VARCHAR(50));
CREATE TABLE Orders(Id INT PRIMARY KEY AUTO_INCREMENT, CustomerId INT, OrderDate DATETIME,
                    Amount DECIMAL(12,2), Status VARCHAR(20));
INSERT INTO Customers VALUES (1,'Rahim','BD','Dhaka'),(2,'John','US','Austin');
INSERT INTO Orders(CustomerId,OrderDate,Amount,Status) VALUES
(1,'2026-06-10',500,'PAID'),(1,'2026-06-12',150,'NEW'),(2,'2026-06-11',300,'PAID');

CREATE INDEX idx_ord_date_status ON Orders(OrderDate, Status, Amount);
CREATE INDEX idx_cust_ctry_city  ON Customers(Country, City, Name);

DELIMITER $$
CREATE PROCEDURE SearchOrders(
  IN p_name VARCHAR(100), IN p_from DATE, IN p_to DATE,
  IN p_min DECIMAL(12,2), IN p_max DECIMAL(12,2), IN p_status VARCHAR(20),
  IN p_country CHAR(2), IN p_city VARCHAR(50),
  IN p_sortby VARCHAR(20), IN p_dir VARCHAR(4), IN p_page INT, IN p_size INT)
BEGIN
  DECLARE v_sort VARCHAR(30); DECLARE v_dir VARCHAR(4); DECLARE v_where TEXT;

  -- allowlists (user input never reaches SQL text)
  SET v_sort = CASE p_sortby WHEN 'CustomerName' THEN 'c.Name' WHEN 'Amount' THEN 'o.Amount'
                             WHEN 'Status' THEN 'o.Status' ELSE 'o.OrderDate' END;
  SET v_dir  = IF(UPPER(p_dir) = 'ASC', 'ASC', 'DESC');
  SET @lim = LEAST(GREATEST(IFNULL(p_size,20),1),100);
  SET @off = (GREATEST(IFNULL(p_page,1),1) - 1) * @lim;

  -- bound values (NULL = filter not supplied)
  SET @n  = CONCAT(p_name,'%');               -- prefix LIKE stays index-friendly
  SET @f  = p_from;  SET @t = DATE_ADD(p_to, INTERVAL 1 DAY);  -- inclusive end date, no DATE() on column
  SET @mn = p_min;   SET @mx = p_max;  SET @st = p_status;  SET @co = p_country;  SET @ci = p_city;

  -- every placeholder is always present; a missing filter becomes a constant "? IS NULL"
  SET v_where = CONCAT(' WHERE 1=1',
    IF(@n  IS NULL,' AND ? IS NULL',' AND c.Name LIKE ?'),
    IF(@f  IS NULL,' AND ? IS NULL',' AND o.OrderDate >= ?'),
    IF(@t  IS NULL,' AND ? IS NULL',' AND o.OrderDate < ?'),
    IF(@mn IS NULL,' AND ? IS NULL',' AND o.Amount >= ?'),
    IF(@mx IS NULL,' AND ? IS NULL',' AND o.Amount <= ?'),
    IF(@st IS NULL,' AND ? IS NULL',' AND o.Status = ?'),
    IF(@co IS NULL,' AND ? IS NULL',' AND c.Country = ?'),
    IF(@ci IS NULL,' AND ? IS NULL',' AND c.City = ?'));

  -- total count
  SET @sql = CONCAT('SELECT COUNT(*) AS Total FROM Orders o JOIN Customers c ON c.Id=o.CustomerId', v_where);
  PREPARE s1 FROM @sql; EXECUTE s1 USING @n,@f,@t,@mn,@mx,@st,@co,@ci; DEALLOCATE PREPARE s1;

  -- page of data (o.Id tiebreaker = stable paging)
  SET @sql = CONCAT('SELECT o.Id, c.Name, c.Country, c.City, o.OrderDate, o.Amount, o.Status ',
    'FROM Orders o JOIN Customers c ON c.Id=o.CustomerId', v_where,
    ' ORDER BY ', v_sort, ' ', v_dir, ', o.Id ', v_dir, ' LIMIT ? OFFSET ?');
  PREPARE s2 FROM @sql; EXECUTE s2 USING @n,@f,@t,@mn,@mx,@st,@co,@ci,@lim,@off; DEALLOCATE PREPARE s2;
END$$
DELIMITER ;

CALL SearchOrders('Ra', '2026-06-01', '2026-06-30', NULL, NULL, 'PAID', 'BD', NULL, 'Amount', 'desc', 1, 10);