import { Component } from '@angular/core';
import { PaymentService, PaymentStatus } from './angular-payment.service';

@Component({
  selector: 'app-payment',
  template: `
    <div class="payment-container">
      <h2>Payment Processing</h2>
      
      <div class="form-group">
        <label>Amount ($):</label>
        <input type="number" [(ngModel)]="amount" step="0.01" min="0.01">
      </div>
      
      <div class="form-group">
        <label>Card Token:</label>
        <input type="text" [(ngModel)]="cardToken" placeholder="tok_demo_123456">
      </div>
      
      <div class="form-group">
        <label>Email (optional):</label>
        <input type="email" [(ngModel)]="email" placeholder="customer@example.com">
      </div>
      
      <button (click)="processPayment()" [disabled]="processing">
        {{ processing ? 'Processing...' : 'Process Payment' }}
      </button>
      
      <div *ngIf="currentPaymentId" class="status-section">
        <h3>Payment ID: <code>{{ currentPaymentId }}</code></h3>
        
        <div class="status-list">
          <div *ngFor="let status of statusHistory" 
               [class]="'status-item status-' + status.status?.toLowerCase()">
            <strong>{{ status.status }}:</strong> {{ status.message }}
            <span class="timestamp">{{ status.timestamp | date:'medium' }}</span>
          </div>
        </div>
      </div>
      
      <div *ngIf="error" class="error">
        {{ error }}
      </div>
    </div>
  `,
  styles: [`
    .payment-container {
      max-width: 600px;
      margin: 50px auto;
      padding: 20px;
      background: white;
      border-radius: 8px;
      box-shadow: 0 2px 4px rgba(0,0,0,0.1);
    }
    .form-group {
      margin-bottom: 15px;
    }
    .form-group label {
      display: block;
      margin-bottom: 5px;
      font-weight: bold;
    }
    .form-group input {
      width: 100%;
      padding: 8px;
      border: 1px solid #ddd;
      border-radius: 4px;
    }
    button {
      background-color: #4CAF50;
      color: white;
      padding: 10px 20px;
      border: none;
      border-radius: 4px;
      cursor: pointer;
      font-size: 16px;
    }
    button:disabled {
      background-color: #cccccc;
      cursor: not-allowed;
    }
    .status-section {
      margin-top: 20px;
      padding: 15px;
      background-color: #f9f9f9;
      border-radius: 4px;
    }
    .status-item {
      padding: 8px;
      margin: 5px 0;
      border-left: 4px solid #4CAF50;
      background-color: white;
    }
    .status-queued { border-left-color: #2196F3; }
    .status-processing { border-left-color: #FF9800; }
    .status-sendingtoprocessor { border-left-color: #9C27B0; }
    .status-completed { border-left-color: #4CAF50; }
    .status-failed { border-left-color: #F44336; }
    .timestamp {
      float: right;
      color: #888;
      font-size: 0.9em;
    }
    .error {
      color: #F44336;
      margin-top: 15px;
      padding: 10px;
      background-color: #ffebee;
      border-radius: 4px;
    }
    code {
      background-color: #f0f0f0;
      padding: 2px 6px;
      border-radius: 3px;
      font-family: monospace;
    }
  `]
})
export class PaymentComponent {
  amount = 99.99;
  cardToken = 'tok_demo_123456';
  email = 'customer@example.com';
  
  processing = false;
  currentPaymentId = '';
  statusHistory: PaymentStatus[] = [];
  error = '';

  constructor(private paymentService: PaymentService) {}

  processPayment(): void {
    if (this.amount <= 0) {
      this.error = 'Please enter a valid amount';
      return;
    }
    
    if (!this.cardToken) {
      this.error = 'Please enter a card token';
      return;
    }

    this.processing = true;
    this.statusHistory = [];
    this.currentPaymentId = '';
    this.error = '';

    this.paymentService.processPayment({
      amount: this.amount,
      cardToken: this.cardToken,
      customerEmail: this.email || undefined
    }).subscribe({
      next: (status) => {
        console.log('Received status:', status);
        
        // First message contains payment ID
        if ('paymentId' in status && !('status' in status)) {
          this.currentPaymentId = status.paymentId;
        } else {
          // Subsequent messages are status updates
          this.statusHistory.push(status as PaymentStatus);
        }
      },
      error: (error) => {
        console.error('Payment error:', error);
        this.error = `Payment failed: ${error.message}`;
        this.processing = false;
      },
      complete: () => {
        console.log('Payment processing complete');
        this.processing = false;
      }
    });
  }
}

